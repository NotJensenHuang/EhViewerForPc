using System.Buffers;
using System.Security.Cryptography;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Core.Download;

public sealed record DownloadMoveProgress(string Phase, long Bytes, long TotalBytes)
{
    public double Percent => TotalBytes == 0 ? 0 : Math.Clamp(Bytes * 100d / TotalBytes, 0, 100);
}

public sealed class DownloadRootMoveJournal
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public string Staging { get; set; } = "";
    public bool Renamed { get; set; }
    public bool FilesReady { get; set; }
    public bool MetadataCommitted { get; set; }
    // Missing fields in older journals err on the side of retaining the directory.
    public bool TargetExisted { get; set; } = true;
    public bool RenamedEntries { get; set; }
    public List<string> EntryNames { get; set; } = [];
}

/// <summary>A recoverable directory transfer. Original bytes remain available until
/// both settings and download records have been durably committed.</summary>
public sealed class DownloadRootMover
{
    private readonly string _journalPath;
    public DownloadRootMoveJournal Journal { get; }
    private DownloadRootMover(string journalPath, DownloadRootMoveJournal journal) { _journalPath = journalPath; Journal = journal; }

    public static DownloadRootMover? OpenPending(AppPaths paths)
    {
        string path = Path.Combine(paths.BaseDirectory, "download-move.json");
        if (!File.Exists(path)) return null;
        var journal = JsonFileStore.LoadOrDefault(path, StorageJsonContext.Default.DownloadRootMoveJournal, () => new(), out var error);
        if (error is not null) throw new IOException("无法读取下载目录迁移记录：" + error);
        journal.EntryNames ??= [];
        Validate(paths, journal.Source, journal.Target);
        string expectedPrefix = journal.Target + ".ehr-move-";
        if (!journal.Staging.StartsWith(expectedPrefix, PathIdentity.Comparison) || journal.Staging.Length != expectedPrefix.Length + 32)
            throw new IOException("下载目录迁移临时路径无效");
        if (journal.EntryNames.Any(n => string.IsNullOrWhiteSpace(n) || n is "." or ".." || Path.GetFileName(n) != n || n.Contains('/') || n.Contains('\\')))
            throw new IOException("下载目录迁移条目无效");
        return new(path, journal);
    }

    public static async Task<DownloadRootMover> PrepareAsync(AppPaths paths, string source, string target,
        IProgress<DownloadMoveProgress>? progress, CancellationToken token, bool allowRename = true)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        Validate(paths, source, target);
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new IOException("新下载目录必须为空，请选择空文件夹，避免覆盖已有文件。");
        // Reject links before any rename or copy: cleanup must stay inside these roots.
        if (Directory.Exists(source)) foreach (var _ in Files(source)) token.ThrowIfCancellationRequested();
        var move = new DownloadRootMover(Path.Combine(paths.BaseDirectory, "download-move.json"), new()
        {
            Source = source, Target = target, Staging = target + ".ehr-move-" + Guid.NewGuid().ToString("N"),
            TargetExisted = Directory.Exists(target),
        });
        move.Save();
        try
        {
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (allowRename && Directory.Exists(source))
            {
                move.Journal.Renamed = !move.Journal.TargetExisted;
                move.Journal.RenamedEntries = move.Journal.TargetExisted;
                if (move.Journal.RenamedEntries) move.Journal.EntryNames = Directory.EnumerateFileSystemEntries(source).Select(Path.GetFileName).Cast<string>().ToList();
                move.Save();
                try
                {
                    progress?.Report(new("正在移动下载目录", 0, 0));
                    token.ThrowIfCancellationRequested();
                    if (move.Journal.RenamedEntries)
                    {
                        foreach (string name in move.Journal.EntryNames)
                        {
                            token.ThrowIfCancellationRequested();
                            MoveEntry(Path.Combine(source, name), Path.Combine(target, name));
                        }
                    }
                    else Directory.Move(source, target);
                    move.Journal.FilesReady = true; move.Save();
                    return move;
                }
                catch (IOException) when (Directory.Exists(source) && (move.Journal.RenamedEntries || !Directory.Exists(target)))
                {
                    if (move.Journal.RenamedEntries) move.RestoreRenamedEntries();
                    move.Journal.Renamed = false; move.Journal.RenamedEntries = false; move.Journal.EntryNames = [];
                    move.Save(); // cross-volume / filesystem fallback
                }
            }
            long total = Directory.Exists(source) ? Files(source).Sum(f => new FileInfo(f).Length) : 0;
            Directory.CreateDirectory(move.Journal.Staging);
            long copied = 0;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                if (Directory.Exists(source))
                    await CopyTree(source, move.Journal.Staging).ConfigureAwait(false);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            token.ThrowIfCancellationRequested();
            move.Journal.EntryNames = Directory.EnumerateFileSystemEntries(move.Journal.Staging).Select(Path.GetFileName).Cast<string>().ToList();
            move.Journal.FilesReady = true; move.Save();
            move.PublishCopiedFiles();
            progress?.Report(new("文件校验完成，正在同步资料库", total, total));
            return move;

            async Task CopyTree(string from, string to)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(from))
                {
                    token.ThrowIfCancellationRequested();
                    var attrs = File.GetAttributes(entry);
                    if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("下载目录含链接，无法安全迁移：" + entry);
                    string destination = Path.Combine(to, Path.GetFileName(entry));
                    if (attrs.HasFlag(FileAttributes.Directory))
                    {
                        Directory.CreateDirectory(destination);
                        await CopyTree(entry, destination).ConfigureAwait(false);
                        continue;
                    }
                    DateTime mtime = File.GetLastWriteTimeUtc(entry);
                    using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        int length;
                        while ((length = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                        {
                            hash.AppendData(buffer.AsSpan(0, length));
                            await output.WriteAsync(buffer.AsMemory(0, length), token).ConfigureAwait(false);
                            copied += length;
                            if (copied == length || timer.ElapsedMilliseconds >= 100) { progress?.Report(new("正在复制下载文件", copied, total)); timer.Restart(); }
                        }
                        await output.FlushAsync(token).ConfigureAwait(false);
                        output.Flush(true);
                    }
                    using (var verification = File.OpenRead(destination))
                    {
                        byte[] digest = await SHA256.HashDataAsync(verification, token).ConfigureAwait(false);
                        if (!digest.AsSpan().SequenceEqual(hash.GetHashAndReset()) || File.GetLastWriteTimeUtc(entry) != mtime)
                            throw new IOException("文件校验失败或源文件被修改：" + entry);
                    }
                    File.SetLastWriteTimeUtc(destination, mtime);
                }
            }
        }
        catch { move.Rollback(); throw; }
    }

    private static void Validate(AppPaths paths, string source, string target)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target)) throw new IOException("下载目录不能为空");
        if (PathIdentity.Comparer.Equals(source, target) || PathIdentity.IsDescendantOf(source, target) || PathIdentity.IsDescendantOf(target, source))
            throw new IOException("新旧下载目录不能相同或互相包含");
        foreach (string root in new[] { source, target })
        {
            if (PathIdentity.Comparer.Equals(root, Path.GetPathRoot(root))) throw new IOException("不能将整个磁盘根目录作为迁移目录");
            if (PathIdentity.Comparer.Equals(root, paths.BaseDirectory) || PathIdentity.IsDescendantOf(paths.BaseDirectory, root) || PathIdentity.IsDescendantOf(root, paths.BaseDirectory))
                throw new IOException("下载目录不能与应用数据目录重叠");
            for (DirectoryInfo? dir = new(root); dir is not null; dir = dir.Parent)
                if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("迁移目录不能包含符号链接或目录联接");
        }
    }

    private static IEnumerable<string> Files(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attrs = File.GetAttributes(entry);
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("下载目录含链接，无法安全迁移：" + entry);
            if (attrs.HasFlag(FileAttributes.Directory)) { foreach (string file in Files(entry)) yield return file; }
            else yield return entry;
        }
    }

    public bool CompletePreparedFiles()
    {
        if (Journal.FilesReady && Directory.Exists(Journal.Staging)) PublishCopiedFiles();
        return Directory.Exists(Journal.Target) && (Journal.FilesReady || (Journal.Renamed && !Directory.Exists(Journal.Source)));
    }

    private static void MoveEntry(string source, string target)
    {
        if (Directory.Exists(source)) Directory.Move(source, target);
        else File.Move(source, target); // never overwrite an existing destination
    }

    private void RestoreRenamedEntries()
    {
        foreach (string name in Journal.EntryNames)
        {
            string source = Path.Combine(Journal.Source, name), target = Path.Combine(Journal.Target, name);
            if (File.Exists(source) || Directory.Exists(source)) continue;
            if (File.Exists(target) || Directory.Exists(target)) MoveEntry(target, source);
        }
    }

    private void PublishCopiedFiles()
    {
        if (!Journal.TargetExisted) { Directory.Move(Journal.Staging, Journal.Target); return; }
        // Keep the selected directory itself, including its permissions/identity.
        Directory.CreateDirectory(Journal.Target);
        foreach (string path in Directory.EnumerateFileSystemEntries(Journal.Staging))
            MoveEntry(path, Path.Combine(Journal.Target, Path.GetFileName(path)));
        Directory.Delete(Journal.Staging); // our staging directory, now empty
    }

    public void Commit() { Journal.MetadataCommitted = true; Save(); }
    public bool Cleanup()
    {
        // Delete only redundant original bytes. Newly changed files stay in place.
        try
        {
            if (!Journal.Renamed && !Journal.RenamedEntries && Directory.Exists(Journal.Source)) CleanupCopies(Journal.Source, Journal.Target);
            else if (Directory.Exists(Journal.Source) && !Directory.EnumerateFileSystemEntries(Journal.Source).Any()) Directory.Delete(Journal.Source);
            bool complete = !Directory.Exists(Journal.Source);
            if (complete) File.Delete(_journalPath);
            return complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    public void Rollback()
    {
        if (Journal.MetadataCommitted) return;
        if (Journal.Renamed && Directory.Exists(Journal.Target) && !Directory.Exists(Journal.Source)) Directory.Move(Journal.Target, Journal.Source);
        else if (Journal.RenamedEntries) RestoreRenamedEntries();
        else if (Directory.Exists(Journal.Source) && Directory.Exists(Journal.Target))
        {
            foreach (string name in Journal.EntryNames)
            {
                // An entry still in staging was never published into the target.
                string staged = Path.Combine(Journal.Staging, name);
                if (File.Exists(staged) || Directory.Exists(staged)) continue;
                string target = Path.Combine(Journal.Target, name), source = Path.Combine(Journal.Source, name);
                if (Directory.Exists(target) && Directory.Exists(source)) CleanupCopies(target, source);
                else if (File.Exists(target) && File.Exists(source)) DeleteIdenticalCopy(target, source);
            }
            if (!Journal.TargetExisted && !Directory.EnumerateFileSystemEntries(Journal.Target).Any()) Directory.Delete(Journal.Target);
        }
        if (Directory.Exists(Journal.Staging)) DeleteOwnedTree(Journal.Staging);
        if (Journal.TargetExisted && !Directory.Exists(Journal.Target)) Directory.CreateDirectory(Journal.Target);
        File.Delete(_journalPath);
    }
    private static void DeleteIdenticalCopy(string path, string other)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(other).HasFlag(FileAttributes.ReparsePoint)) return;
        if (new FileInfo(path).Length != new FileInfo(other).Length) return;
        bool equal;
        using (var a = File.OpenRead(path)) using (var b = File.OpenRead(other)) equal = SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
        if (equal) File.Delete(path);
    }
    private static void CleanupCopies(string from, string to)
    {
        if (File.GetAttributes(from).HasFlag(FileAttributes.ReparsePoint) || File.GetAttributes(to).HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (string path in Directory.EnumerateFileSystemEntries(from))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) continue;
            string other = Path.Combine(to, Path.GetFileName(path));
            try
            {
                if (Directory.Exists(path)) { if (Directory.Exists(other)) CleanupCopies(path, other); }
                else if (File.Exists(other) && new FileInfo(path).Length == new FileInfo(other).Length)
                {
                    DeleteIdenticalCopy(path, other);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (!Directory.EnumerateFileSystemEntries(from).Any()) Directory.Delete(from);
    }
    private static void DeleteOwnedTree(string root)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("迁移临时目录含链接");
            if (Directory.Exists(path)) DeleteOwnedTree(path); else File.Delete(path);
        }
        Directory.Delete(root);
    }
    private void Save()
    {
        if (!JsonFileStore.Save(_journalPath, Journal, StorageJsonContext.Default.DownloadRootMoveJournal, out string? error)) throw new IOException(error);
    }
}
