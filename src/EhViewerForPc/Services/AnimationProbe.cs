using System.Diagnostics;
using System.Text;
using EhViewerForPc.Core.Storage;
using SkiaSharp;

namespace EhViewerForPc.Services;

/// <summary>
/// Read-only measurement for one animated file: frame count, per-frame decode
/// cost (kept codec vs. a fresh codec per frame) and allocation churn. Exists so
/// "this GIF stutters" reports can be diagnosed without guessing; no window is
/// created and the file is only opened for reading.
/// </summary>
public static class AnimationProbe
{
    public static async Task<int> RunAsync(string path, string? outputPath, string? dumpFrames = null)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine("anim-probe: file not found: " + path);
            return 2;
        }

        string dataDirectory = Path.Combine(Path.GetTempPath(), "EhReaderAnimProbe-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(dataDirectory);
        paths.EnsureCreated();
        AppServices services = AppServices.Initialize(paths);
        ImageDecoder decoder = services.Images.Decoder;
        var report = new Dictionary<string, object?>();
        var console = new StringBuilder();

        var file = new FileInfo(path);
        report["file"] = path;
        report["bytes"] = file.Length;

        long allocBefore = GC.GetTotalAllocatedBytes(precise: true);
        int gen2Before = GC.CollectionCount(2);

        DecodedImage? first = await decoder.DecodeAsync(path, null, null, 24, 16000, CancellationToken.None);
        if (first is null)
        {
            Console.Error.WriteLine("anim-probe: decode failed: " + path);
            return 3;
        }

        report["storedSize"] = $"{first.Bitmap.PixelSize.Width}x{first.Bitmap.PixelSize.Height}";
        report["decodedSize"] = $"{first.DecodedWidth}x{first.DecodedHeight}";
        report["isAnimated"] = first.IsAnimated;
        report["frameCount"] = first.FrameCount;

        var openWatch = Stopwatch.StartNew();
        AnimationCursor? cursor = decoder.TryOpenAnimationCursor(path, null, null, 24, 16000);
        openWatch.Stop();
        report["cursorOpenMs"] = Math.Round(openWatch.Elapsed.TotalMilliseconds, 2);
        if (cursor is null || !first.IsAnimated || first.FrameCount < 2)
        {
            Console.WriteLine("anim-probe: not an animated file.");
            first.Dispose();
            return 4;
        }

        // Per-frame durations straight from the container.
        var durations = new List<int>(cursor.FrameCount);
        for (int i = 0; i < cursor.FrameCount; i++) durations.Add(cursor.FrameDurationMs(i));
        report["durationMinMs"] = durations.Min();
        report["durationMaxMs"] = durations.Max();
        report["durationAvgMs"] = Math.Round(durations.Average(), 1);

        // ① kept codec + recycled buffers (what playback uses).
        var keptTimes = new List<double>(cursor.FrameCount);
        var total = Stopwatch.StartNew();
        for (int i = 1; i < cursor.FrameCount; i++)
        {
            var watch = Stopwatch.StartNew();
            DecodedImage? frame = await cursor.DecodeFrameAsync(i, CancellationToken.None);
            watch.Stop();
            keptTimes.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 2));
            frame?.Dispose();
        }

        total.Stop();
        report["keptCodecTotalMs"] = Math.Round(total.Elapsed.TotalMilliseconds, 1);
        report["keptCodecAvgMs"] = Math.Round(keptTimes.Average(), 2);
        report["keptCodecMaxMs"] = keptTimes.Max();
        report["keptCodecFirstMs"] = keptTimes[0];
        report["keptCodecLastMs"] = keptTimes[^1];

        // ② loop wrap: frame 0 after the last frame (a fresh pass in the file).
        var wrapWatch = Stopwatch.StartNew();
        DecodedImage? wrapped = await cursor.DecodeFrameAsync(0, CancellationToken.None);
        wrapWatch.Stop();
        report["wrapMs"] = Math.Round(wrapWatch.Elapsed.TotalMilliseconds, 2);
        wrapped?.Dispose();

        // ③ a fresh codec per frame (the previous implementation), sampled.
        var sampled = new[] { 1, 5, 20, 40, 60, 90, 120, 180 }
            .Where(i => i < cursor.FrameCount)
            .ToArray();
        var freshTimes = new List<double>(sampled.Length);
        foreach (int index in sampled)
        {
            var watch = Stopwatch.StartNew();
            DecodedImage? frame = await decoder.DecodeFrameAsync(path, index, null, null, 24, 16000, CancellationToken.None);
            watch.Stop();
            freshTimes.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 2));
            frame?.Dispose();
        }

        report["freshCodecFrames"] = sampled;
        report["freshCodecMs"] = freshTimes;

        // ④ reader-sized decode (what the page view asks for).
        AnimationCursor? viewport = decoder.TryOpenAnimationCursor(path, 1200, 1000, 24, 16000);
        if (viewport is not null)
        {
            var viewTimes = new List<double>();
            for (int i = 1; i < Math.Min(viewport.FrameCount, 12); i++)
            {
                var watch = Stopwatch.StartNew();
                DecodedImage? frame = await viewport.DecodeFrameAsync(i, CancellationToken.None);
                watch.Stop();
                viewTimes.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 2));
                frame?.Dispose();
            }

            report["viewportAvgMs"] = viewTimes.Count > 0 ? Math.Round(viewTimes.Average(), 2) : 0;
            report["viewportMaxMs"] = viewTimes.Count > 0 ? viewTimes.Max() : 0;
            viewport.Dispose();
        }

        // ④b Decode-only allocation: isolates what the frame path itself costs,
        // without the pacing/timer machinery of the player loop.
        AnimationFramePool.ResetCounters();
        var decodeAllocCursor = decoder.TryOpenAnimationCursor(path, null, null, 24, 16000);
        if (decodeAllocCursor is not null)
        {
            long decodeAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
            int decodeFrames = 0;
            for (int i = 1; i < Math.Min(decodeAllocCursor.FrameCount, 41); i++)
            {
                DecodedImage? frame = await decodeAllocCursor.DecodeFrameAsync(i, CancellationToken.None);
                frame?.Dispose();
                decodeFrames++;
            }

            long decodeAllocAfter = GC.GetTotalAllocatedBytes(precise: true);
            report["decodeAllocPerFrameBytes"] = decodeFrames > 0
                ? (decodeAllocAfter - decodeAllocBefore) / decodeFrames
                : 0;
            report["poolDecode"] = AnimationFramePool.Describe();
            decodeAllocCursor.Dispose();
        }

        // ⑤ Playback simulation through the real player loop (no window): does it
        // keep the declared cadence, and does it ever stall?
        DecodedImage? playbackFirst = await decoder.DecodeAsync(path, null, null, 24, 16000, CancellationToken.None);
        if (playbackFirst is not null && playbackFirst.IsAnimated)
        {
            var presented = new List<long>();
            long playAllocBefore = GC.GetTotalAllocatedBytes(precise: true);
            int playGen0Before = GC.CollectionCount(0);
            int playGen2Before = GC.CollectionCount(2);
            var playWatch = Stopwatch.StartNew();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string reason = await AnimationPlayer.PlayAsync(
                playbackFirst,
                _ =>
                {
                    presented.Add(playWatch.ElapsedMilliseconds);
                    return Task.FromResult(true);
                },
                stop.Token);
            playWatch.Stop();
            playbackFirst.Dispose();

            long playAllocAfter = GC.GetTotalAllocatedBytes(precise: true);
            var gaps = new List<long>();
            for (int i = 1; i < presented.Count; i++) gaps.Add(presented[i] - presented[i - 1]);
            var sorted = gaps.OrderBy(g => g).ToList();
            long Percentile(double p) => sorted.Count == 0
                ? 0
                : sorted[Math.Clamp((int)Math.Round(p * (sorted.Count - 1)), 0, sorted.Count - 1)];

            int declaredMs = report.GetValueOrDefault("durationAvgMs") is double avg ? (int)Math.Round(avg) : 50;
            report["playFrames"] = presented.Count;
            report["playSeconds"] = Math.Round(playWatch.Elapsed.TotalSeconds, 2);
            report["playFps"] = presented.Count > 0
                ? Math.Round(presented.Count / Math.Max(0.001, playWatch.Elapsed.TotalSeconds), 1)
                : 0;
            report["playMaxGapMs"] = gaps.Count > 0 ? gaps.Max() : 0;
            report["playGapP50Ms"] = Percentile(0.50);
            report["playGapP95Ms"] = Percentile(0.95);
            report["playGapP99Ms"] = Percentile(0.99);
            report["playLateFrames"] = gaps.Count(g => g > declaredMs * 1.5);
            report["playAllocPerFrameBytes"] = presented.Count > 0
                ? (playAllocAfter - playAllocBefore) / presented.Count
                : 0;
            report["playGen0"] = GC.CollectionCount(0) - playGen0Before;
            report["playGen2"] = GC.CollectionCount(2) - playGen2Before;
            report["playStopReason"] = reason;
            console.AppendLine($"playback 5 s     : {report["playFrames"]} frames · {report["playFps"]} fps · " +
                                $"gap p50/p95/max {report["playGapP50Ms"]}/{report["playGapP95Ms"]}/{report["playMaxGapMs"]} ms · " +
                                $"late {report["playLateFrames"]}");
            console.AppendLine($"playback alloc   : {report["playAllocPerFrameBytes"]} B/frame · " +
                                $"gen0 {report["playGen0"]} · gen2 {report["playGen2"]} · stop={reason}");
            console.AppendLine($"decode alloc     : {report.GetValueOrDefault("decodeAllocPerFrameBytes")} B/frame · " +
                                $"pool [{report.GetValueOrDefault("poolDecode")}]");
        }

        // ⑥ Correctness: the incremental composition must be pixel-identical to a
        // canonical decode of the same frame through a fresh codec (Skia rebuilds
        // the chain from frame 0 in that path).
        var compareCursor = decoder.TryOpenAnimationCursor(path, null, null, 24, 16000);
        if (compareCursor is not null)
        {
            var results = new List<string>();
            foreach (int index in new[] { Math.Min(2, compareCursor.FrameCount - 1), compareCursor.FrameCount / 2, compareCursor.FrameCount - 1 }.Distinct())
            {
                DecodedImage? incremental = await compareCursor.DecodeFrameAsync(index, CancellationToken.None);
                DecodedImage? reference = await decoder.DecodeFrameAsync(path, index, null, null, 24, 16000, CancellationToken.None);
                if (incremental is not null && reference is not null)
                {
                    (long differing, int maxDelta, long pixelTotal) = CompareFrames(incremental, reference);
                    results.Add($"f{index}:{differing}/{pixelTotal}px Δ{maxDelta}");
                    report[$"composeMatch_{index}"] = new
                    {
                        differingPixels = differing,
                        totalPixels = pixelTotal,
                        maxChannelDelta = maxDelta,
                        identical = differing == 0,
                    };
                }

                incremental?.Dispose();
                reference?.Dispose();
            }

            compareCursor.Dispose();
            console.AppendLine($"compose check    : {string.Join(" · ", results)}");
        }

        // ⑦ Optional visual check: write a few composed frames as PNG so the
        // delta composition can be inspected without the UI.
        if (!string.IsNullOrWhiteSpace(dumpFrames))
        {
            Directory.CreateDirectory(dumpFrames);
            AnimationCursor? dumpCursor = decoder.TryOpenAnimationCursor(path, null, null, 24, 16000);
            if (dumpCursor is not null)
            {
                var wanted = new[] { 0, 1, 2, 10, 30, dumpCursor.FrameCount / 2, dumpCursor.FrameCount - 1 }
                    .Where(i => i >= 0 && i < dumpCursor.FrameCount)
                    .Distinct()
                    .OrderBy(i => i)
                    .ToArray();
                var written = new List<string>();
                foreach (int index in wanted)
                {
                    DecodedImage? frame = await dumpCursor.DecodeFrameAsync(index, CancellationToken.None);
                    if (frame is null) continue;
                    string target = Path.Combine(dumpFrames, $"frame-{index:000}.png");
                    SaveFramePng(frame, target);
                    written.Add(target);
                    frame.Dispose();
                }

                dumpCursor.Dispose();
                report["dumpedFrames"] = written;
                console.AppendLine($"dumped frames    : {string.Join(", ", written.Select(Path.GetFileName))}");
            }
        }

        cursor.Dispose();
        first.Dispose();

        long allocAfter = GC.GetTotalAllocatedBytes(precise: true);
        report["managedAllocPerFrameKb"] =
            Math.Round((allocAfter - allocBefore) / 1024.0 / Math.Max(1, keptTimes.Count + sampled.Length), 1);
        report["gen2Collections"] = GC.CollectionCount(2) - gen2Before;

        console.AppendLine($"file             : {path}  ({file.Length / 1048576.0:F1} MB)");
        console.AppendLine($"format / frames  : {DescribeHeader(path)} · {first.FrameCount} frames · {report["storedSize"]}");
        console.AppendLine($"frame duration   : {report["durationMinMs"]}–{report["durationMaxMs"]} ms (avg {report["durationAvgMs"]})");
        console.AppendLine($"kept codec       : avg {report["keptCodecAvgMs"]} ms · max {report["keptCodecMaxMs"]} ms · " +
                            $"first {report["keptCodecFirstMs"]} · last {report["keptCodecLastMs"]} · total {report["keptCodecTotalMs"]} ms");
        console.AppendLine($"cursor open      : {report["cursorOpenMs"]} ms (container re-parse for playback)");
        console.AppendLine($"loop wrap        : {report["wrapMs"]} ms");
        console.AppendLine($"fresh codec/frame: {string.Join(", ", freshTimes)} ms  (frames {string.Join(",", sampled)})");
        console.AppendLine($"viewport decode  : avg {report.GetValueOrDefault("viewportAvgMs")} ms · max {report.GetValueOrDefault("viewportMaxMs")} ms");
        console.AppendLine($"alloc / frame    : {report["managedAllocPerFrameKb"]} KB managed · gen2 GCs {report["gen2Collections"]}");
        Console.Write(console.ToString());
        Console.Out.Flush();

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            string target = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(target, BuildJson(report, console.ToString()));
            Console.WriteLine("report: " + target);
        }

        return 0;
    }

    /// <summary>Pixel comparison used to prove delta composition equals a canonical decode.</summary>
    private static (long Differing, int MaxDelta, long Total) CompareFrames(DecodedImage left, DecodedImage right)
    {
        if (left.Bitmap is not Avalonia.Media.Imaging.WriteableBitmap a ||
            right.Bitmap is not Avalonia.Media.Imaging.WriteableBitmap b)
        {
            return (-1, -1, 0);
        }

        using var la = a.Lock();
        using var lb = b.Lock();
        if (la.Size != lb.Size) return (-1, -1, 0);

        long differing = 0;
        int maxDelta = 0;
        int rowBytes = Math.Min(la.RowBytes, lb.RowBytes);
        var rowA = new byte[rowBytes];
        var rowB = new byte[rowBytes];
        for (int y = 0; y < la.Size.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(la.Address + y * la.RowBytes, rowA, 0, rowBytes);
            System.Runtime.InteropServices.Marshal.Copy(lb.Address + y * lb.RowBytes, rowB, 0, rowBytes);
            for (int x = 0; x < la.Size.Width; x++)
            {
                int offset = x * 4;
                int delta = Math.Max(
                    Math.Max(Math.Abs(rowA[offset] - rowB[offset]), Math.Abs(rowA[offset + 1] - rowB[offset + 1])),
                    Math.Max(Math.Abs(rowA[offset + 2] - rowB[offset + 2]), Math.Abs(rowA[offset + 3] - rowB[offset + 3])));
                if (delta > 0)
                {
                    differing++;
                    if (delta > maxDelta) maxDelta = delta;
                }
            }
        }

        return (differing, maxDelta, (long)la.Size.Width * la.Size.Height);
    }

    /// <summary>Writes one decoded frame as PNG (BGRA framebuffer → Skia image).</summary>
    private static void SaveFramePng(DecodedImage frame, string path)
    {
        if (frame.Bitmap is not Avalonia.Media.Imaging.WriteableBitmap writeable) return;
        using var locked = writeable.Lock();
        var info = new SKImageInfo(
            locked.Size.Width, locked.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = new SKBitmap();
        if (!surface.InstallPixels(info, locked.Address, locked.RowBytes)) return;
        using SKImage image = SKImage.FromBitmap(surface);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        using FileStream stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static string DescribeHeader(string path)
    {
        Span<byte> head = stackalloc byte[12];
        using (FileStream stream = ImageDecoder.OpenRead(path))
        {
            int read = stream.Read(head);
            if (read >= 6 && head[0] == 'G' && head[1] == 'I' && head[2] == 'F') return "GIF";
            if (read >= 12 && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return "WebP";
            if (read >= 8 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G') return "APNG/PNG";
            return "unknown";
        }
    }

    private static string BuildJson(Dictionary<string, object?> report, string console)
    {
        var builder = new StringBuilder();
        builder.AppendLine("{");
        foreach ((string key, object? value) in report)
        {
            builder.Append("  \"").Append(key).Append("\": ");
            builder.Append(value switch
            {
                null => "null",
                string text => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
                bool flag => flag ? "true" : "false",
                int or long or double or float => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
                IEnumerable<int> numbers => "[" + string.Join(", ", numbers) + "]",
                IEnumerable<double> numbers => "[" + string.Join(", ", numbers) + "]",
                _ => "\"" + value + "\"",
            });
            builder.AppendLine(",");
        }

        builder.AppendLine("  \"console\": \"" + console.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
