using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EhViewerForPc.Core.Format;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using SkiaSharp;

namespace EhViewerForPc.Services;

/// <summary>
/// Unattended verification pass: scan the real library (read-only), decode real
/// covers/pages, measure timings and memory, and prove the library was untouched.
/// </summary>
public static class SelfTestService
{
    public static async Task<int> RunAsync(StartupOptions options)
    {
        var report = new Dictionary<string, object?>();
        var failures = new List<string>();
        string root = options.Root ?? string.Empty;

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.Error.WriteLine("self-test: --root <path> is required and must exist.");
            return 2;
        }

        string dataDirectory = options.DataDirectory
                               ?? Path.Combine(Path.GetTempPath(), "EhReaderSelfTest-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dataDirectory);
        paths.EnsureCreated();
        AppServices services = AppServices.Initialize(paths);
        services.Settings.LibraryRoot = root;
        report["root"] = root;
        report["dataDirectory"] = dataDirectory;
        report["machine"] = DescribeMachine();

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 扫描资料库: {root}");
        Console.Out.Flush();

        // Synthetic long-strip decode: the real library may or may not contain
        // one, but the memory-budget path must always be exercised.
        Phase("合成长条图");
        try
        {
            string syntheticDir = Path.Combine(dataDirectory, "synthetic");
            Directory.CreateDirectory(syntheticDir);
            string stripPath = Path.Combine(syntheticDir, "long-strip-977x14998.jpg");
            CreateLongStrip(stripPath);
            var stripWatch = Stopwatch.StartNew();
            DecodedImage? syntheticStrip = await services.Images.LoadFileAsync(stripPath, 1200, 0, CancellationToken.None);
            stripWatch.Stop();
            report["syntheticLongStrip"] = new
            {
                ms = Math.Round(stripWatch.Elapsed.TotalMilliseconds, 1),
                decodedWidth = syntheticStrip?.DecodedWidth,
                decodedHeight = syntheticStrip?.DecodedHeight,
                megabytes = syntheticStrip is null ? 0 : Math.Round(syntheticStrip.ApproximateBytes / 1048576.0, 1),
            };
            if (syntheticStrip is null || syntheticStrip.DecodedWidth != 977 || syntheticStrip.DecodedHeight != 14998)
            {
                failures.Add("合成长条图解码尺寸异常");
            }

            syntheticStrip?.Dispose();
            File.Delete(stripPath);
            Phase("合成长条图完成");
        }
        catch (Exception ex)
        {
            failures.Add("合成长条图失败: " + ex.Message);
        }
        (LibrarySnapshot? before, string? beforeHash) = options.SkipSnapshot
            ? (null, null)
            : Snapshot(root);

        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Processed % 1000 == 0 && p.Total > 0)
            {
                Console.WriteLine($"  {p.Phase}: {p.Processed:N0}/{p.Total:N0}");
            }
        });

        var coldWatch = Stopwatch.StartNew();
        ScanResult cold;
        try
        {
            cold = services.Scanner.Scan(root, null, forceRefresh: true, progress, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("扫描失败: " + ex);
            return 1;
        }

        coldWatch.Stop();
        Phase($"冷扫描完成 {coldWatch.Elapsed.TotalSeconds:F1}s");
        report["coldScanSeconds"] = Math.Round(coldWatch.Elapsed.TotalSeconds, 2);

        int totalPages = cold.Galleries.Sum(g => g.PageCount);
        var stats = new Dictionary<string, object?>
        {
            ["galleries"] = cold.Galleries.Count,
            ["pages"] = totalPages,
            ["emptyGalleries"] = cold.Galleries.Count(g => g.PageCount == 0),
            ["missingGalleries"] = cold.Galleries.Count(g => g.HasMissingPages),
            ["zeroByteGalleries"] = cold.Galleries.Count(g => g.ZeroByteFiles > 0),
            ["noHeader"] = cold.Galleries.Count(g => !g.HasEhviewer),
            ["noThumb"] = cold.Galleries.Count(g => !g.HasThumb),
            ["duplicateGidGalleries"] = cold.Galleries.Count(g => g.IsDuplicateGid),
            ["gidConflictGalleries"] = cold.Galleries.Count(g => g.HasGidConflict),
            ["noGidGalleries"] = cold.Galleries.Count(g => g.Gid is null),
            ["gidFromHeaderOnly"] = cold.Galleries.Count(g => g.DirectoryGid is null && g.Gid.HasValue),
            ["maxPages"] = cold.Galleries.Count == 0 ? 0 : cold.Galleries.Max(g => g.PageCount),
            ["missingRanges"] = cold.Galleries.Sum(g => g.MissingRanges.Count),
            ["ignoredDirectories"] = cold.IgnoredDirectoryCount,
            ["cacheHitsCold"] = cold.CacheHitCount,
        };
        report["library"] = stats;

        if (cold.Galleries.Count < 6000) failures.Add($"画廊数量过低: {cold.Galleries.Count}");
        if (totalPages < 240_000) failures.Add($"页数过低: {totalPages}");

        string? cacheError;
        bool cacheSaved = services.CacheStore.Save(cold.Cache, out cacheError);
        report["cacheSaved"] = cacheSaved;
        if (!cacheSaved) failures.Add("缓存保存失败: " + cacheError);

        Phase("读取缓存");
        LibraryCache warmCache = services.CacheStore.Load(root, out _);
        Phase("热扫描开始");
        var warmWatch = Stopwatch.StartNew();
        ScanResult warm = services.Scanner.Scan(root, warmCache, forceRefresh: false, progress: null);
        warmWatch.Stop();
        Phase($"热扫描完成 {warmWatch.Elapsed.TotalSeconds:F1}s，命中 {warm.CacheHitCount}");
        report["warmScanSeconds"] = Math.Round(warmWatch.Elapsed.TotalSeconds, 2);
        report["warmCacheHits"] = warm.CacheHitCount;
        report["warmScanned"] = warm.ScannedDirectoryCount;

        // Decode checks -----------------------------------------------------
        var decodeResults = new List<object>();
        var samples = cold.Galleries
            .Where(g => g.PageCount > 0 && !string.IsNullOrWhiteSpace(g.CoverPath))
            .OrderBy(g => g.PageCount)
            .Take(12)
            .ToList();

        Phase("封面解码 " + samples.Count + " 个");
        foreach (GalleryInfo gallery in samples)
        {
            Phase("cover: " + gallery.FolderName);
            var watch = Stopwatch.StartNew();
            DecodedImage? cover = await services.Covers.LoadCoverAsync(gallery, 360, 0, CancellationToken.None);
            watch.Stop();
            decodeResults.Add(new
            {
                kind = "cover",
                gid = gallery.Gid,
                pageCount = gallery.PageCount,
                ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                ok = cover is not null,
                width = cover?.DecodedWidth,
                height = cover?.DecodedHeight,
                megabytes = cover is null ? 0 : Math.Round(cover.ApproximateBytes / 1048576.0, 1),
            });
            if (cover is null) failures.Add("封面解码失败: " + gallery.FolderName);
            Phase("cover done: " + (cover is null ? "null" : $"{cover.DecodedWidth}x{cover.DecodedHeight}"));
            cover?.Dispose();
        }

        Phase("最大画廊页面解码");
        GalleryInfo? largest = cold.Galleries.MaxBy(g => g.PageCount);
        if (largest is not null && largest.PageCount > 0)
        {
            PageScanResult pages = PageFileParser.Scan(largest.FolderPath, null, out _);
            var indices = new[] { 0, pages.PageCount / 2, pages.PageCount - 1 }
                .Distinct()
                .Where(i => i >= 0 && i < pages.PageCount);
            foreach (int index in indices)
            {
                GalleryPage page = pages.Pages[index];
                Phase("page: " + largest.FolderName + " #" + page.Number);
                var request = new PageImageRequest(page.Candidates, services.Images);
                var watch = Stopwatch.StartNew();
                DecodedImage? image = await request.LoadAsync(1200, 0, CancellationToken.None);
                watch.Stop();
                decodeResults.Add(new
                {
                    kind = "page",
                    gid = largest.Gid,
                    page = page.Number,
                    ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                    ok = image is not null,
                    width = image?.DecodedWidth,
                    height = image?.DecodedHeight,
                    megabytes = image is null ? 0 : Math.Round(image.ApproximateBytes / 1048576.0, 1),
                });
                if (image is null) failures.Add($"页面解码失败: {largest.FolderName} #{page.Number}");
                image?.Dispose();
            }

            var dimWatch = Stopwatch.StartNew();
            IReadOnlyList<PageDimension> dimensions = await services.Dimensions
                .LoadAsync(largest, pages.Pages, null, CancellationToken.None);
            dimWatch.Stop();
            report["largestDimensions"] = new
            {
                pages = dimensions.Count,
                unknown = dimensions.Count(d => !d.Known),
                seconds = Math.Round(dimWatch.Elapsed.TotalSeconds, 2),
            };
        }

        // Long-strip and max-pixel samples.
        (GalleryInfo Gallery, GalleryPage Page, int Width, int Height)? longStrip = null;
        (GalleryInfo Gallery, GalleryPage Page, int Width, int Height)? biggest = null;
        int probed = 0;
        Phase(options.DeepProbe ? "尺寸全量探测" : "尺寸抽样");
        int galleryLimit = options.DeepProbe ? int.MaxValue : 80;
        int pagesPerGallery = options.DeepProbe ? int.MaxValue : 3;
        int galleryIndex = 0;
        foreach (GalleryInfo gallery in cold.Galleries.Where(g => g.PageCount > 0).Take(galleryLimit))
        {
            galleryIndex++;
            if (options.DeepProbe && galleryIndex % 500 == 0)
            {
                Phase($"  探测 {galleryIndex}/{cold.Galleries.Count} · {probed} 文件");
            }

            PageScanResult scan = PageFileParser.Scan(gallery.FolderPath, null, out _);
            foreach (GalleryPage page in scan.Pages.Take(pagesPerGallery))
            {
                PageCandidate? candidate = page.Candidates.FirstOrDefault(c => c.Length > 0);
                if (candidate is null) continue;
                if (options.DeepProbe)
                {
                    Phase($"probe {candidate.Length}B {candidate.FilePath}");
                }

                (int width, int height)? dims = ReadDimensions(candidate.FilePath);
                probed++;
                if (dims is null) continue;
                (int w, int h) = dims.Value;
                if (longStrip is null && h > w * 6)
                {
                    longStrip = (gallery, page, w, h);
                }

                if (biggest is null || (long)w * h > (long)biggest.Value.Width * biggest.Value.Height)
                {
                    biggest = (gallery, page, w, h);
                }
            }
        }

        Phase($"尺寸抽样完成 {probed} 个文件");
        report["dimensionProbes"] = probed;
        report["deepProbe"] = options.DeepProbe;
        if (longStrip is { } strip)
        {
            var request = new PageImageRequest(strip.Page.Candidates, services.Images);
            var watch = Stopwatch.StartNew();
            DecodedImage? image = await request.LoadAsync(1200, 0, CancellationToken.None);
            watch.Stop();
            report["longStrip"] = new
            {
                gid = strip.Gallery.Gid,
                page = strip.Page.Number,
                sourceWidth = strip.Width,
                sourceHeight = strip.Height,
                ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                decodedWidth = image?.DecodedWidth,
                decodedHeight = image?.DecodedHeight,
                megabytes = image is null ? 0 : Math.Round(image.ApproximateBytes / 1048576.0, 1),
            };
            if (image is null) failures.Add("长条图解码失败");
            image?.Dispose();
        }
        else
        {
            report["longStrip"] = null;
            Console.WriteLine("提示: 抽样中未发现长宽比 > 6 的长条图");
        }

        if (biggest is { } max)
        {
            var request = new PageImageRequest(max.Page.Candidates, services.Images);
            var watch = Stopwatch.StartNew();
            DecodedImage? image = await request.LoadAsync(1600, 1200, CancellationToken.None);
            watch.Stop();
            report["largestImage"] = new
            {
                gid = max.Gallery.Gid,
                page = max.Page.Number,
                sourceWidth = max.Width,
                sourceHeight = max.Height,
                ms = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
                decodedWidth = image?.DecodedWidth,
                decodedHeight = image?.DecodedHeight,
                megabytes = image is null ? 0 : Math.Round(image.ApproximateBytes / 1048576.0, 1),
            };
            if (image is null) failures.Add("最大图解码失败");
            image?.Dispose();
        }

        // Zero-byte page must fail gracefully, not throw.
        GalleryInfo? zeroByteGallery = cold.Galleries.FirstOrDefault(g => g.ZeroByteFiles > 0);
        if (zeroByteGallery is not null)
        {
            PageScanResult scan = PageFileParser.Scan(zeroByteGallery.FolderPath, null, out _);
            GalleryPage? broken = scan.Pages.FirstOrDefault(p => p.Candidates.All(c => c.Length == 0));
            if (broken is not null)
            {
                var request = new PageImageRequest(broken.Candidates, services.Images);
                DecodedImage? image = await request.LoadAsync(1200, 0, CancellationToken.None);
                report["zeroBytePage"] = new
                {
                    gid = zeroByteGallery.Gid,
                    page = broken.Number,
                    returnedNull = image is null,
                };
                if (image is not null) failures.Add("0 字节页面被解码出了一张图");
                image?.Dispose();
            }
        }

        Phase("长条图/最大图/0 字节检查完成");
        // Memory / read-only -------------------------------------------------
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using (Process process = Process.GetCurrentProcess())
        {
            report["managedHeapMb"] = Math.Round(GC.GetTotalMemory(true) / 1048576.0, 1);
            report["peakWorkingSetMb"] = Math.Round(process.PeakWorkingSet64 / 1048576.0, 1);
            report["workingSetMb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1);
        }

        if (!options.SkipSnapshot)
        {
            (LibrarySnapshot? after, string? afterHash) = Snapshot(root);
            bool identical = before is not null && after is not null && before == after && beforeHash == afterHash;
            report["libraryUnchanged"] = identical;
            report["librarySnapshot"] = new
            {
                files = after?.FileCount ?? 0,
                directories = after?.DirectoryCount ?? 0,
                bytes = after?.TotalBytes ?? 0,
                hashBefore = beforeHash,
                hashAfter = afterHash,
            };
            if (before is null) failures.Add("资料库快照失败（扫描前）");
            if (after is null) failures.Add("资料库快照失败（扫描后）");
            if (!identical) failures.Add("资料库在自检期间发生变化（可能有写入）");
        }

        report["decodes"] = decodeResults;
        report["failures"] = failures;
        report["pass"] = failures.Count == 0;
        report["finishedUtc"] = DateTime.UtcNow.ToString("O");

        Phase("写入报告");
        string output = options.OutputPath
                        ?? Path.Combine(dataDirectory, "selftest.json");
        await DiagnosticReport.WriteAsync(output, report);

        Console.WriteLine();
        Console.WriteLine($"画廊 {stats["galleries"]:N0} · 页 {totalPages:N0} · 冷扫描 {report["coldScanSeconds"]}s · " +
                          $"热扫描 {report["warmScanSeconds"]}s（缓存命中 {warm.CacheHitCount:N0}）");
        Console.WriteLine("报告: " + output);
        if (failures.Count > 0)
        {
            Console.WriteLine("失败项:");
            foreach (string failure in failures) Console.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine("自检通过。");
        return 0;
    }

    private static void CreateLongStrip(string path)
    {
        using var bitmap = new SKBitmap(977, 14998);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(24, 28, 40));
        using var band = new SKPaint { Color = new SKColor(200, 80, 96), IsAntialias = true };
        for (int y = 400; y < 14998; y += 900)
        {
            canvas.DrawRect(0, y, 977, 40, band);
        }

        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 82);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static void Phase(string message)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
        Console.Out.Flush();
    }

    private static string DescribeMachine() =>
        $"{Environment.MachineName} · {Environment.OSVersion.VersionString} · .NET {Environment.Version} · " +
        $"{Environment.ProcessorCount} cores · {CultureInfo.CurrentCulture.Name}";

    private static (int Width, int Height)? ReadDimensions(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
            using SKCodec? codec = SKCodec.Create(stream, out SKCodecResult result);
            if (codec is null || result != SKCodecResult.Success) return null;
            return (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return null;
        }
    }

    private sealed record LibrarySnapshot(int FileCount, int DirectoryCount, long TotalBytes);

    private static (LibrarySnapshot Snapshot, string Hash) SnapshotWithHash(string root)
    {
        int files = 0;
        int directories = 0;
        long bytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            directories++;
            hash.AppendData(Encoding.UTF8.GetBytes("D|" + Path.GetRelativePath(root, directory) + "\n"));
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            files++;
            bytes += info.Length;
            hash.AppendData(Encoding.UTF8.GetBytes(
                "F|" + Path.GetRelativePath(root, file) + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks + "\n"));
        }

        string digest = Convert.ToHexString(hash.GetHashAndReset());
        return (new LibrarySnapshot(files, directories, bytes), digest);
    }

    private static (LibrarySnapshot? Snapshot, string? Hash) Snapshot(string root)
    {
        try
        {
            (LibrarySnapshot snapshot, string hash) = SnapshotWithHash(root);
            return (snapshot, hash);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("快照失败: " + ex.Message);
            return (null, null);
        }
    }
}
