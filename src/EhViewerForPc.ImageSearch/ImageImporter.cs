using System.Formats.Tar;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace EhViewerForPc.ImageSearch;

/// <summary>Defines bounded import and deduplication behavior.</summary>
public sealed record ImportOptions
{
    /// <summary>Gets the number of extraction workers, limited to two.</summary>
    public int Workers { get; init; } = 2;
    /// <summary>Gets the low-feature skip threshold (inclusive).</summary>
    public int MinFeatures { get; init; } = 30;
    /// <summary>Gets the accepted input extensions.</summary>
    public string[] Suffixes { get; init; } = ["jpg", "jpeg", "png", "webp", "gif"];
    /// <summary>Gets an optional regular expression matching relative aliases.</summary>
    public string? Filter { get; init; }
    /// <summary>Gets whether a duplicate image retains additional aliases.</summary>
    public bool Append { get; init; } = true;
    /// <summary>Gets whether duplicate content replaces its existing alias set.</summary>
    public bool Overwrite { get; init; }
    /// <summary>Gets an optional alias rewrite regular expression.</summary>
    public string? ReplacePattern { get; init; }
    /// <summary>Gets the alias rewrite replacement.</summary>
    public string Replacement { get; init; } = "";
    /// <summary>Gets the maximum perceptual duplicate distance.</summary>
    public int DuplicateDistance { get; init; }
}

/// <summary>Summarizes a complete import operation.</summary>
public sealed record ImportSummary(int Total, int Added, int AlreadyPresent, int Skipped, int Failed, bool Complete);

/// <summary>Imports read-only files through bounded extraction and transactional database writes.</summary>
public static class ImageImporter
{
    private sealed record Input(string Alias, string? File, byte[]? Bytes, string? Error = null);
    private sealed record Extracted(string Alias, byte[]? Hash, BinaryDescriptor[]? Descriptors, string? Error);

    /// <summary>Rejects overlapping or redirected writable paths before any backend files are created.</summary>
    public static void ValidateImportPaths(string backendDirectory, string inputPath, string reportPath)
    {
        string input = Path.GetFullPath(inputPath), backend = Path.GetFullPath(backendDirectory), report = Path.GetFullPath(reportPath);
        if (!Directory.Exists(input) && !File.Exists(input)) throw new FileNotFoundException("Import input is unavailable.");
        if (Directory.Exists(input) && (IsWithin(backend, input) || IsWithin(input, backend) || IsWithin(report, input)))
            throw new InvalidOperationException("Writable backend and report paths must be separate from the read-only source directory.");
        if (Directory.Exists(input))
            for (DirectoryInfo? info = new(input); info is not null; info = info.Parent)
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Use the physical source path instead of a junction or symbolic link.");
        foreach (string path in new[] { backend, Path.GetDirectoryName(report)! })
        {
            for (DirectoryInfo? info = new(path); info is not null; info = info.Parent)
                if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Writable import paths cannot traverse junctions or symbolic links.");
        }
    }

    /// <summary>Imports a directory or a tar archive and atomically writes a complete JSONL report.</summary>
    public static async Task<ImportSummary> ImportAsync(ImageDatabase database, string inputPath, string reportPath,
        ImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        database.RequireWriter(); options ??= new();
        if (options.Workers is < 1 or > 2 || options.MinFeatures < 0 || options.DuplicateDistance is < 0 or > 64)
            throw new ArgumentOutOfRangeException(nameof(options));
        string input = Path.GetFullPath(inputPath), report = Path.GetFullPath(reportPath);
        ValidateImportPaths(database.DirectoryPath, input, report);
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        string temporary = report + "." + Guid.NewGuid().ToString("N") + ".part";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken ct = cancellation.Token;
        var inputs = Channel.CreateBounded<Input>(new BoundedChannelOptions(2) { SingleWriter = true });
        var outputs = Channel.CreateBounded<Extracted>(new BoundedChannelOptions(2) { SingleReader = true });
        // Only one pixel-owning extraction is allowed at a time; workers still overlap IO, writes and scheduling.
        using var canvas = new SemaphoreSlim(1, 1);
        using var tarPayload = new SemaphoreSlim(1, 1);
        Task producer = Task.Run(async () =>
        {
            try
            {
                var accepted = new HashSet<string>(options.Suffixes.Select(s => s.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
                Regex? filter = options.Filter is null ? null : new Regex(options.Filter, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                Regex? rewrite = options.ReplacePattern is null ? null : new Regex(options.ReplacePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                string replacement = Regex.Replace(options.Replacement, @"\$([A-Za-z_][A-Za-z0-9_]*)", match => "${" + match.Groups[1].Value + "}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                bool Accept(string alias) => accepted.Contains(Path.GetExtension(alias).TrimStart('.')) && (filter is null || filter.IsMatch(alias));
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string MapAlias(string value)
                {
                    string mapped = rewrite?.Replace(value, replacement) ?? value;
                    ImageDatabase.ValidateAlias(mapped);
                    if (!seen.Add(mapped)) throw new InvalidDataException("Duplicate input alias.");
                    return mapped;
                }
                if (Directory.Exists(input))
                {
                    if ((File.GetAttributes(input) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Source junctions are not traversed.");
                    var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                    foreach (string file in Directory.EnumerateFiles(input, "*", enumeration))
                    {
                        ct.ThrowIfCancellationRequested();
                        string alias = Path.GetRelativePath(input, file).Replace('\\', '/');
                        if (!Accept(alias)) continue;
                        alias = MapAlias(alias);
                        await inputs.Writer.WriteAsync(new(alias, file, null), ct);
                    }
                }
                else
                {
                    using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var tar = new TarReader(source);
                    TarEntry? entry;
                    while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
                    {
                        if (entry.EntryType == TarEntryType.Directory) { ImageDatabase.ValidateAlias(entry.Name.TrimEnd('/')); continue; }
                        ImageDatabase.ValidateAlias(entry.Name);
                        if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("Tar links and special entries are not accepted.");
                        if (!Accept(entry.Name)) continue;
                        string alias = MapAlias(entry.Name);
                        if (entry.Length is < 1 or > 50 * 1024 * 1024)
                        { await inputs.Writer.WriteAsync(new(alias, null, null, "InputTooLarge"), ct); continue; }
                        await tarPayload.WaitAsync(ct);
                        byte[] bytes = new byte[checked((int)entry.Length)];
                        if (entry.DataStream is null) throw new InvalidDataException("Missing tar payload.");
                        await entry.DataStream.ReadExactlyAsync(bytes, ct);
                        await inputs.Writer.WriteAsync(new(alias, null, bytes), ct);
                    }
                }
                inputs.Writer.TryComplete();
            }
            catch (Exception error) { inputs.Writer.TryComplete(error); cancellation.Cancel(); throw; }
        }, ct);
        Task[] workers = Enumerable.Range(0, options.Workers).Select(_ => Task.Run(async () =>
        {
            try
            {
                var extractor = new OrbExtractor(database.Profile);
                await foreach (var item in inputs.Reader.ReadAllAsync(ct))
                {
                    if (item.Error is not null) { await outputs.Writer.WriteAsync(new(item.Alias, null, null, item.Error), ct); continue; }
                    Extracted extracted;
                    await canvas.WaitAsync(ct);
                    try
                    {
                        byte[] bytes = item.Bytes ?? ReadSource(item.File!);
                        GrayImage image = GrayImage.Decode(bytes);
                        byte[] hash = database.HashMode == ImageHashMode.Blake3 ? Blake3.Hash(bytes) : image.DifferenceHash();
                        var descriptors = extractor.Extract(image, ct).Select(f => f.Descriptor).ToArray();
                        extracted = new(item.Alias, hash, descriptors, null);
                    }
                    catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException)
                    { extracted = new(item.Alias, null, null, error.Message.StartsWith("InputTooLarge", StringComparison.Ordinal) ? "InputTooLarge" : error.Message.StartsWith("SourceChanging", StringComparison.Ordinal) ? "SourceChanging" : "InvalidInput"); }
                    finally { canvas.Release(); if (item.Bytes is not null) tarPayload.Release(); }
                    await outputs.Writer.WriteAsync(extracted, ct);
                }
            }
            catch { cancellation.Cancel(); throw; }
        }, ct)).ToArray();
        Task completion = Task.Run(async () =>
        {
            try { await Task.WhenAll(workers); outputs.Writer.TryComplete(); }
            catch (Exception error) { outputs.Writer.TryComplete(error); throw; }
        }, CancellationToken.None);
        try
        {
            int total = 0, added = 0, present = 0, skipped = 0, failed = 0;
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream))
            {
                await foreach (var item in outputs.Reader.ReadAllAsync(ct))
                {
                    ImportEntry entry;
                    if (item.Error is not null) entry = new(item.Alias, "failed", 0, item.Error);
                    else entry = database.Add(item.Alias, item.Hash!, item.Descriptors!, options.MinFeatures, options.Append, options.DuplicateDistance, options.Overwrite);
                    total++;
                    switch (entry.Status) { case "added": added++; break; case "already_present": present++; break; case "skipped_low_features": skipped++; break; default: failed++; break; }
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry, SearchJsonContext.Default.ImportEntry));
                }
                await producer; await completion;
                var summary = new ImportSummary(total, added, present, skipped, failed, true);
                await writer.WriteLineAsync(JsonSerializer.Serialize(summary, SearchJsonContext.Default.ImportSummary));
                await writer.FlushAsync(ct); stream.Flush(true);
            }
            File.Move(temporary, report, overwrite: true);
            return new(total, added, present, skipped, failed, true);
        }
        finally
        {
            cancellation.Cancel(); inputs.Writer.TryComplete(); outputs.Writer.TryComplete();
            try { await Task.WhenAll(producer, completion); } catch { /* All tasks are observed; preserve the primary error. */ }
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] ReadSource(string path)
    {
        var info = new FileInfo(path); long length = info.Length, time = info.LastWriteTimeUtc.Ticks;
        if (length is < 1 or > 50 * 1024 * 1024) throw new InvalidDataException("InputTooLarge.");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[checked((int)length)]; source.ReadExactly(bytes);
        info.Refresh();
        if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != time) throw new IOException("SourceChanging.");
        return bytes;
    }

    internal static bool IsWithin(string path, string root)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
