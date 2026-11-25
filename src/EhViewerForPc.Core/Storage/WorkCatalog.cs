using EhViewerForPc.Core.Models;

namespace EhViewerForPc.Core.Storage;

public sealed class WorkCatalog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly DeferredSaveQueue _writes;
    private readonly Dictionary<string, WorkRecord> _works;

    public event Action<string>? LanguageChanged;

    public WorkCatalog(AppPaths paths, DeferredSaveQueue writes)
    {
        _path = Path.Combine(paths.BaseDirectory, "works.json");
        _writes = writes;
        _works = JsonFileStore.LoadOrDefault(_path, StorageJsonContext.Default.WorkCatalogFile,
            () => new WorkCatalogFile(), out _).Works;
    }

    public WorkRecord? Get(string id)
    {
        lock (_gate) return _works.GetValueOrDefault(id)?.Clone();
    }

    public string? GetLanguage(string id)
    {
        lock (_gate) return _works.GetValueOrDefault(id)?.Metadata?.Language;
    }

    public void MoveIdentity(string previous, string current)
    {
        if (previous == current) return;
        lock (_gate)
        {
            if (!_works.Remove(previous, out var work)) return;
            work.WorkId = current;
            _works[current] = work;
        }
        _writes.Schedule("works", Save);
    }

    public void Upsert(WorkRecord work)
    {
        bool languageChanged;
        lock (_gate)
        {
            if (_works.TryGetValue(work.WorkId, out var previous))
            {
                if (string.IsNullOrEmpty(work.Token)) work.Token = previous.Token;
                if (string.IsNullOrEmpty(work.ThumbnailUrl)) work.ThumbnailUrl = previous.ThumbnailUrl;
                work.Metadata ??= previous.Metadata?.Clone();
            }
            languageChanged = !string.Equals(previous?.Metadata?.Language ?? string.Empty,
                work.Metadata?.Language ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            _works[work.WorkId] = work.Clone();
        }
        _writes.Schedule("works", Save);
        if (languageChanged) LanguageChanged?.Invoke(work.WorkId);
    }

    private void Save()
    {
        WorkCatalogFile snapshot;
        lock (_gate) snapshot = new() { Works = _works.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal) };
        JsonFileStore.Save(_path, snapshot, StorageJsonContext.Default.WorkCatalogFile, out _);
    }
}
