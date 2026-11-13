using EhViewerForPc.Core.Models;
using EhViewerForPc.ImageSearch;

namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    private async Task ReclaimRetiredAsync()
    {
        try
        {
            while (IsBuilding) await Task.Delay(100, _lifetime.Token);
            if (Volatile.Read(ref _disposed) == 0) await CleanupAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _services.Log.Warn("Retired index cleanup: " + error.Message); }
    }

    private static string SharedFeaturesPath(string root) => Path.Combine(root, "features.db");

    private static void RemoveInterruptedTemporaryFiles(string generation, CancellationToken token)
    {
        foreach (string file in Directory.EnumerateFiles(generation, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            if (name.EndsWith(".part", StringComparison.Ordinal) || name == "invlists.new.bin") File.Delete(file);
        }
    }

    private static IEnumerable<string> OwnedGenerations(string root)
    {
        string parent = Path.Combine(root, "generations");
        if (!Directory.Exists(parent)) yield break;
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || ContainsReparsePoint(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            string marker = Path.Combine(directory, ".ehreader-image-search-generation");
            if (File.Exists(marker) && File.ReadAllText(marker) == "ehreader-image-search/1") yield return directory;
        }
    }

    private void MigrateOwnedFeatureCaches(string root, LibrarySource[] sources, CancellationToken token)
    {
        string cache = SharedFeaturesPath(root);
        if (File.Exists(cache) && (File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("共享特征库不能是链接。");
        foreach (string generation in OwnedGenerations(root))
        {
            token.ThrowIfCancellationRequested();
            string backend = Path.Combine(generation, "backend");
            if (!File.Exists(Path.Combine(backend, "images.db"))) continue;
            ValidateStorage(backend, sources);
            using var database = new ImageDatabase(backend, writable: true);
            database.UseSharedFeatureCache(cache, migrateExisting: true, token);
        }
    }

    private ImageIndexBuildCheckpoint? FindLegacyPendingBuild(string root, ImageSearchGallery[] galleries, LibrarySource[] sources)
    {
        string? current, previous;
        lock (_generationGate)
        {
            current = LoadAvailablePointer(root)?.GenerationId;
            previous = ReadPointer(root, backup: true)?.GenerationId;
        }
        string? selected = null;
        int mostPages = 0;
        foreach (string generation in OwnedGenerations(root))
        {
            string id = Path.GetFileName(generation);
            if (id == current || id == previous || !File.Exists(Path.Combine(generation, "backend", "images.db"))) continue;
            var pending = new ImageIndexBuildCheckpoint { GenerationId = id, ProfileId = new FeatureProfile().Id };
            int pages = CountCheckpointPages(root, pending);
            if (pages > mostPages) { selected = id; mostPages = pages; }
        }
        if (selected is null) return null;
        using var database = new ImageDatabase(Path.Combine(root, "generations", selected, "backend"));
        if (database.Profile.Id != new FeatureProfile().Id) return null;
        return new ImageIndexBuildCheckpoint
        {
            GenerationId = selected, PreviousGenerationId = current ?? string.Empty, ProfileId = database.Profile.Id,
            ConfigurationId = ConfigurationId(root, sources), Phase = ImageIndexBuildPhase.Extracting,
            Galleries = galleries.Select(g => new ImageIndexGalleryCheckpoint(g.SourceId, g.StableId, g.WorkId, g.Folder)).ToArray(),
        };
    }

    private void PruneSharedFeatures(string root)
    {
        if (!File.Exists(SharedFeaturesPath(root)) || LoadBuildCheckpoint(root) is not null) return;
        IEnumerable<(string Profile, byte[] Hash)> References()
        {
            foreach (string generation in OwnedGenerations(root))
            {
                string backend = Path.Combine(generation, "backend");
                if (!File.Exists(Path.Combine(backend, "images.db"))) continue;
                using var database = new ImageDatabase(backend);
                string profile = database.Profile.Id;
                foreach (byte[] hash in database.ReferencedFeatureHashes()) yield return (profile, hash);
            }
        }
        using var cache = new SharedFeatureCache(SharedFeaturesPath(root));
        cache.Prune(References());
    }
}
