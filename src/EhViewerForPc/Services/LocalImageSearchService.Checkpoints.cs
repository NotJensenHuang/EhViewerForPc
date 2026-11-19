using System.Security.Cryptography;
using System.Text;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Storage;
using EhViewerForPc.ImageSearch;
using Microsoft.Data.Sqlite;

namespace EhViewerForPc.Services;

public sealed partial class LocalImageSearchService
{
    private static string CheckpointPath(string root) => Path.Combine(root, "build-job.json");

    private ImageIndexBuildCheckpoint? LoadBuildCheckpoint(string root)
    {
        lock (_generationGate) return ReadBuildCheckpoint(root);
    }

    private ImageIndexBuildCheckpoint? ReadBuildCheckpoint(string root)
    {
        var job = JsonFileStore.LoadOrDefault(CheckpointPath(root), StorageJsonContext.Default.ImageIndexBuildCheckpoint,
            () => new ImageIndexBuildCheckpoint(), out string? error);
        if (error is not null) _services.Log.Warn("Image index checkpoint: " + error);
        if (job.Galleries is null || job.Galleries.Any(g => g is null || string.IsNullOrWhiteSpace(g.SourceId) ||
                string.IsNullOrWhiteSpace(g.StableId) || string.IsNullOrWhiteSpace(g.Folder)) ||
            string.IsNullOrEmpty(job.ConfigurationId) || !Enum.IsDefined(job.Phase) ||
            job.PreviousGenerationId is null || job.SchemaVersion != 1 || !Guid.TryParseExact(job.GenerationId, "N", out _) ||
            (job.PreviousGenerationId.Length > 0 && !Guid.TryParseExact(job.PreviousGenerationId, "N", out _)) ||
            job.ProfileId != new FeatureProfile().Id) return null;
        return job;
    }

    private static string ConfigurationId(string root, LibrarySource[] sources)
    {
        static string Canonical(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
        string value = Canonical(root) + "\n" + string.Join("\n", sources.OrderBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => s.Id + "|" + Canonical(s.RootPath)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void SaveBuildCheckpoint(string root, ImageIndexBuildCheckpoint job)
    {
        lock (_generationGate)
        {
            if (!JsonFileStore.Save(CheckpointPath(root), job, StorageJsonContext.Default.ImageIndexBuildCheckpoint, out string? error))
                throw new IOException("无法保存索引检查点：" + error);
        }
    }

    private void CompleteBuildCheckpoint(string root)
    {
        lock (_generationGate)
        {
            try { File.Delete(CheckpointPath(root)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _services.Log.Warn("Completed index checkpoint cleanup: " + error.Message); }
        }
    }

    private static int CountCheckpointPages(string root, ImageIndexBuildCheckpoint job)
    {
        string generation = Path.Combine(root, "generations", job.GenerationId);
        if (!File.Exists(Path.Combine(generation, "manifest.db"))) return 0;
        try
        {
            using var manifest = OpenManifest(generation, writable: false);
            using var count = manifest.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM member";
            return checked((int)(long)count.ExecuteScalar()!);
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return 0; }
    }

    /// <summary>只接续上次正常退出 / 意外中断的任务；显式取消的任务等待用户点击继续。</summary>
    public async Task<bool> ResumePendingBuildAsync(IReadOnlyList<ImageSearchGallery>? currentGalleries = null, CancellationToken token = default)
    {
        if (!_services.Settings.ImageSearch.Enabled || IsBuilding || Volatile.Read(ref _disposed) != 0) return false;
        string root = DirectoryPath;
        var selected = SelectedSources();
        var allSources = _services.Settings.Sources.Select(s => s.Clone()).ToArray();
        var job = await RunReadAsync(readToken =>
        {
            readToken.ThrowIfCancellationRequested();
            ValidateStorage(root, allSources);
            return LoadBuildCheckpoint(root) ?? (currentGalleries is not { Count: > 0 } ? null : FindLegacyPendingBuild(root, currentGalleries.ToArray(), selected));
        }, token).ConfigureAwait(true);
        if (job is null || !job.ResumeOnStartup || IsBuilding) return false;
        if (job.ConfigurationId != ConfigurationId(root, SelectedSources()))
        {
            PublishBuildStatus(new("存在未完成索引，数据源设置已变化，请重新建立索引", CanResume: true));
            return false;
        }
        var galleries = job.Galleries.Select(g => new ImageSearchGallery(g.SourceId, g.StableId, g.WorkId, g.Folder)).ToArray();
        await BuildCoreAsync(galleries, null, token, job.Rebuild, resumeExisting: true, automatic: true,
            changedFolders: job.ChangedFolders).ConfigureAwait(true);
        return true;
    }
}
