using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Services;

/// <summary>Runs the real application service and reader models without creating an Avalonia application or window.</summary>
internal static class LocalImageSearchAppProbe
{
    internal static async Task<int> RunAsync(string root, string? output)
    {
        var checks = new List<AppImageSearchProbeCheck>(); string? error = null;
        try
        {
            root = Path.GetFullPath(root);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Probe root must be empty.");
            var paths = new AppPaths(Path.Combine(root, "data")); paths.EnsureCreated();
            var settings = new AppSettings(); settings.Download.RootPath = Path.Combine(root, "downloads"); settings.Download.AddAsSource = false; settings.Download.AutoStart = false;
            new SettingsStore(paths).Save(settings, out _);
            using var services = AppServices.Initialize(paths);
            await services.ImageSearch.GetStatusAsync();
            checks.Add(new("disabled-no-index", !Directory.Exists(services.ImageSearch.DirectoryPath)));
            var galleries = new List<GalleryInfo>();
            for (int copy = 0; copy < 2; copy++)
            {
                string sourcePath = Path.Combine(root, "source-" + copy), folder = Path.Combine(sourcePath, "42-Fixture"); Directory.CreateDirectory(folder);
                var source = services.Settings.AddSource(sourcePath, "Fixture " + copy);
                foreach (int page in new[] { 1, 3, 5, 7 }) File.WriteAllBytes(Path.Combine(folder, $"{page:D8}.png"), SyntheticImages.Create(20261112 + page, 240, 320).EncodePng());
                galleries.Add(new GalleryInfo { StableId = "copy-" + copy, SourceId = source.Id, SourceName = source.DisplayName, WorkId = "42", Gid = 42,
                    Title = "Fixture", FolderName = "42-Fixture", FolderPath = folder, PageCount = 4, ExpectedPages = 7 });
            }
            services.Settings.ImageSearch.Enabled = true;
            var built = await services.ImageSearch.BuildAsync(galleries.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray(), null, CancellationToken.None);
            checks.Add(new("application-build", built.Indexed == 8));
            var updated = await services.ImageSearch.BuildAsync(galleries.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.FolderPath)).ToArray(), null, CancellationToken.None);
            checks.Add(new("incremental-reuses-pages", updated.Indexed == 8 && updated.Reused == 8));
            checks.Add(new("single-published-snapshot", Directory.GetDirectories(Path.Combine(services.ImageSearch.DirectoryPath, "generations")).Length == 1));
            checks.Add(new("shared-features", File.Exists(Path.Combine(services.ImageSearch.DirectoryPath, "features.db"))));
            checks.Add(new("completed-checkpoint-removed", !File.Exists(Path.Combine(services.ImageSearch.DirectoryPath, "build-job.json"))));
            byte[] query = File.ReadAllBytes(Path.Combine(galleries[0].FolderPath, "00000005.png"));
            var hits = await services.ImageSearch.SearchAsync(query, null, galleries, CancellationToken.None);
            checks.Add(new("exact-copy-mapping", hits.Any(h => h.Gallery.SourceId == galleries[1].SourceId && h.PageNumber == 5)));
            services.Progress.Set("42", new ReadingProgress { PageNumber = 3, PageOffset = .8, Completed = true });
            using (var reader = new ReaderViewModel(services, galleries[1], initialPageNumber: 5))
            {
                checks.Add(new("reader-explicit-page", reader.PageLabel == "5 / 7" && !reader.ShowRestoreHint));
                checks.Add(new("reader-repeated-page", reader.TryNavigateToPageNumber(5) && reader.PageOffsetForTest == 0));
                checks.Add(new("reader-missing-page", reader.TryNavigateToPageNumber(2) && reader.IsCurrentPageMissing && reader.PageLabel == "2 / 7"));
            }
            services.Settings.Sources[1].Enabled = false;
            hits = await services.ImageSearch.SearchAsync(query, null, galleries, CancellationToken.None);
            checks.Add(new("disabled-source-filter", hits.All(h => h.Gallery.SourceId != galleries[1].SourceId)));
            var current = hits.First(h => h.PageNumber == 5);
            File.Delete(current.FilePath);
            checks.Add(new("deleted-page-filter", !services.ImageSearch.CanRead(current)));
        }
        catch (Exception failure) { error = failure.GetType().Name + ": " + failure.Message; checks.Add(new("unhandled", false)); }
        string destination = output ?? Path.Combine(root, "app-probe.json");
        var report = new AppImageSearchProbeReport("ManagedCSharp", checks.All(c => c.Passed), checks.ToArray(), error);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, AppImageSearchProbeJsonContext.Default.AppImageSearchProbeReport));
        return report.Passed ? 0 : 3;
    }
}
internal sealed record AppImageSearchProbeCheck(string Id, bool Passed);
internal sealed record AppImageSearchProbeReport(string BackendKind, bool Passed, AppImageSearchProbeCheck[] Checks, string? Error);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppImageSearchProbeReport))]
internal partial class AppImageSearchProbeJsonContext : JsonSerializerContext;
