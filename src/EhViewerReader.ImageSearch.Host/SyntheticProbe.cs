using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EhViewerReader.ImageSearch;

namespace EhViewerReader.ImageSearch.Host;

internal sealed record ProbeCheck(string Id, bool Passed, string Message);
internal sealed record ProbeReport(int SchemaVersion, string Mode, string BackendKind, string BackendBuildId, string ProfileId,
    DateTime StartedUtc, DateTime FinishedUtc, int Passed, int Failed, int Images, int CropQueries, int CropTop10Hits,
    double CropP95Milliseconds, long PeakWorkingSet, ProbeCheck[] Checks, string? Error);

internal static class SyntheticProbe
{
    internal static async Task<int> RunAsync(string root, string output)
    {
        DateTime started = DateTime.UtcNow; var checks = new List<ProbeCheck>();
        string profile = ""; int cropHits = 0; var latencies = new List<double>(); string? error = null;
        try
        {
            root = Path.GetFullPath(root); output = Path.GetFullPath(output);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Probe directory must be empty.");
            string sources = Path.Combine(root, "images"), backend = Path.Combine(root, "backend");
            Directory.CreateDirectory(sources);
            for (int i = 0; i < 64; i++) await File.WriteAllBytesAsync(Path.Combine(sources, $"page-{i:D3}.png"), SyntheticImages.Create(20261112 + i).EncodePng());
            var before = Directory.EnumerateFiles(sources).Order().Select(f => (Path: f, Length: new FileInfo(f).Length, Time: File.GetLastWriteTimeUtc(f))).ToArray();
            using (var database = new ImageDatabase(backend, writable: true))
            {
                profile = database.Profile.Id;
                var imported = await ImageImporter.ImportAsync(database, sources, Path.Combine(root, "import.jsonl"));
                checks.Add(new("synthetic-import", imported.Added == 64 && imported.Failed == 0, $"added={imported.Added}, failed={imported.Failed}, skipped={imported.Skipped}"));
                var trained = ImageSearchEngine.Train(database, 64, 64, iterations: 8);
                checks.Add(new("two-level-training", trained.Centers.Length == 64, $"centers={trained.Centers.Length}"));
                var partial = ImageSearchEngine.Build(database, batchSize: 16, stopAfterSegments: 1);
                checks.Add(new("safe-build-pause", !partial.Complete, "Paused after one durable segment."));
            }
            using (var database = new ImageDatabase(backend, writable: true))
            {
                var built = ImageSearchEngine.Build(database, batchSize: 16);
                checks.Add(new("resume-build", built.Complete && built.IndexedImages == 64, $"images={built.IndexedImages}, vectors={built.IndexedVectors}"));
                database.CheckIntegrity(); checks.Add(new("sqlite-integrity", true, "PRAGMA integrity_check=ok"));
                var engine = new ImageSearchEngine(database);
                for (int i = 0; i < 8; i++)
                {
                    string file = Path.Combine(sources, $"page-{i:D3}.png");
                    var exact = engine.Search(GrayImage.ReadFile(file));
                    checks.Add(new($"exact-{i}", exact.Length > 0 && exact[0].Aliases.Contains($"page-{i:D3}.png"), "Original page ranks first."));
                    var query = GrayImage.ReadFile(file, new ImageCrop(47, 66, 226, 316));
                    var clock = Stopwatch.StartNew(); var cropped = engine.Search(query); latencies.Add(clock.Elapsed.TotalMilliseconds);
                    if (cropped.Take(10).Any(hit => hit.Aliases.Contains($"page-{i:D3}.png"))) cropHits++;
                }
                checks.Add(new("synthetic-crop-search", cropHits >= 7, $"Top10 hits={cropHits}/8; synthetic fixtures only, not real comic recall."));
                var blank = new GrayImage(200, 200, Enumerable.Repeat((byte)255, 40000).ToArray());
                await File.WriteAllBytesAsync(Path.Combine(root, "blank.png"), blank.EncodePng());
                checks.Add(new("blank-query", engine.Search(blank).Length == 0, "Blank image returns success with no candidates."));
                var replay = await ImageImporter.ImportAsync(database, sources, Path.Combine(root, "replay.jsonl"));
                checks.Add(new("idempotent-import", replay.AlreadyPresent == 64 && replay.Added == 0, $"already_present={replay.AlreadyPresent}"));
                var rebuilt = ImageSearchEngine.Build(database);
                checks.Add(new("idempotent-build", rebuilt.IndexedVectors == built.IndexedVectors && rebuilt.IndexedImages == 64, "Repeated build does not duplicate vectors."));
                database.Clean(); checks.Add(new("clean-descriptor-cache", !database.ReadCached().Any(), "Indexed descriptor cache removed."));
            }
            using (var database = new ImageDatabase(backend))
            {
                var engine = new ImageSearchEngine(database);
                var hits = engine.Search(GrayImage.ReadFile(Path.Combine(sources, "page-003.png")));
                checks.Add(new("reload-after-clean", hits.Length > 0 && hits[0].Aliases.Contains("page-003.png"), "Read-only reload searches the published compressed IVF."));
            }
            checks.Add(new("source-read-only", before.All(f => new FileInfo(f.Path).Length == f.Length && File.GetLastWriteTimeUtc(f.Path) == f.Time)
                && Directory.EnumerateFileSystemEntries(sources).Count() == 64, "Source paths, lengths and modification times remain unchanged."));
        }
        catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; checks.Add(new("unhandled", false, error)); }
        var process = Process.GetCurrentProcess(); process.Refresh();
        var report = new ProbeReport(1, "synthetic", "ManagedCSharp", Program.BuildId, profile, started, DateTime.UtcNow,
            checks.Count(c => c.Passed), checks.Count(c => !c.Passed), 64, latencies.Count, cropHits,
            latencies.Count == 0 ? 0 : latencies.Order().ElementAt((int)Math.Ceiling(latencies.Count * .95) - 1), process.PeakWorkingSet64, checks.ToArray(), error);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, HostJsonContext.Default.ProbeReport));
        return report.Failed == 0 ? 0 : 3;
    }
}
