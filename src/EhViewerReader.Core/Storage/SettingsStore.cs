using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Storage;

public sealed class SettingsStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    public AppSettings Load(out string? error)
    {
        var settings = JsonFileStore.LoadOrDefault(
            _paths.SettingsFile, StorageJsonContext.Default.AppSettings, () => new AppSettings(), out error);
        return settings.Normalize();
    }

    public bool Save(AppSettings settings, out string? error)
    {
        lock (_gate)
        {
            settings.Normalize();
            return JsonFileStore.Save(_paths.SettingsFile, settings, StorageJsonContext.Default.AppSettings, out error);
        }
    }
}

public sealed class ProgressFile
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, ReadingProgress> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProgressStore
{
    private readonly AppPaths _paths;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReadingProgress> _entries;
    private bool _dirty;

    public ProgressStore(AppPaths paths)
    {
        _paths = paths;
        var file = JsonFileStore.LoadOrDefault(
            _paths.ProgressFile, StorageJsonContext.Default.ProgressFile, () => new ProgressFile(), out string? error);
        LoadError = error;
        if (file.SchemaVersion != 1)
        {
            file = new ProgressFile();
        }

        _entries = new Dictionary<string, ReadingProgress>(file.Entries, StringComparer.OrdinalIgnoreCase);
    }

    public string? LoadError { get; }
    public bool IsDirty => _dirty;

    public ReadingProgress? Get(string stableId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(stableId, out var progress) ? progress.Clone() : null;
        }
    }

    public ReadingProgress GetOrCreate(string stableId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(stableId, out var existing)) return existing.Clone();
            var created = new ReadingProgress { LastReadUtc = DateTime.UtcNow };
            _entries[stableId] = created;
            _dirty = true;
            return created.Clone();
        }
    }

    public bool Favorite(string stableId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(stableId, out var progress) && progress.Favorite;
        }
    }

    public void Set(string stableId, ReadingProgress progress)
    {
        lock (_gate)
        {
            _entries[stableId] = progress.Clone();
            _dirty = true;
        }
    }

    public void RecordRead(string stableId, int pageNumber, int pageIndex, double pageOffset, int pageCount, bool completed)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(stableId, out var progress))
            {
                progress = new ReadingProgress();
                _entries[stableId] = progress;
            }

            progress.PageNumber = Math.Max(1, pageNumber);
            progress.PageIndex = Math.Max(0, pageIndex);
            progress.PageOffset = Math.Clamp(pageOffset, 0, 1);
            progress.PageCountAtSave = Math.Max(0, pageCount);
            progress.LastReadUtc = DateTime.UtcNow;
            progress.HiddenFromHistory = false;
            progress.Completed = completed;
            _dirty = true;
        }
    }

    public void ToggleFavorite(string stableId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(stableId, out var progress))
            {
                progress = new ReadingProgress();
                _entries[stableId] = progress;
            }

            progress.Favorite = !progress.Favorite;
            _dirty = true;
        }
    }

    public void MarkRead(string stableId, int pageCount)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(stableId, out var progress))
            {
                progress = new ReadingProgress();
                _entries[stableId] = progress;
            }

            progress.PageNumber = Math.Max(1, pageCount);
            progress.PageIndex = Math.Max(0, pageCount - 1);
            progress.PageOffset = 0;
            progress.PageCountAtSave = Math.Max(0, pageCount);
            progress.LastReadUtc = DateTime.UtcNow;
            progress.Completed = pageCount > 0;
            _dirty = true;
        }
    }

    public void MarkUnread(string stableId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(stableId, out var progress))
            {
                progress = new ReadingProgress();
                _entries[stableId] = progress;
            }

            progress.PageNumber = 1;
            progress.PageIndex = 0;
            progress.PageOffset = 0;
            progress.Completed = false;
            progress.LastReadUtc = default;
            progress.HiddenFromHistory = true;
            _dirty = true;
        }
    }

    public void Clear(string stableId)
    {
        lock (_gate)
        {
            if (_entries.Remove(stableId)) _dirty = true;
        }
    }

    public IReadOnlyDictionary<string, ReadingProgress> Snapshot()
    {
        lock (_gate) return _entries.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    public void HideFromHistory(string workId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(workId, out var progress)) { progress.HiddenFromHistory = true; _dirty = true; }
        }
    }

    public void ResetReading(string workId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(workId, out var progress)) return;
            _entries[workId] = new ReadingProgress { Favorite = progress.Favorite, HiddenFromHistory = true };
            _dirty = true;
        }
    }

    public bool Save(out string? error)
    {
        lock (_gate)
        {
            if (!_dirty) { error = null; return true; }
            var file = new ProgressFile
            {
                SchemaVersion = 1,
                Entries = new Dictionary<string, ReadingProgress>(_entries, StringComparer.OrdinalIgnoreCase),
            };
            bool saved = JsonFileStore.Save(_paths.ProgressFile, file, StorageJsonContext.Default.ProgressFile, out error);
            if (saved) _dirty = false;
            return saved;
        }
    }
}
