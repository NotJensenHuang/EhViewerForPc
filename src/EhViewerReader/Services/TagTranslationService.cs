using System.IO.Compression;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;
using EhViewerReader.Core.Storage;

namespace EhViewerReader.Services;

/// <summary>
/// Owns the EhTagTranslation database: lazy load from disk, explicit update
/// from the community release, and the display strings used by the tag wall.
/// </summary>
public sealed class TagTranslationService
{
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly EhClient _client;
    private readonly LogService _log;
    private readonly object _gate = new();

    private TagTranslationDatabase? _database;
    private bool _loadAttempted;
    private bool _updating;
    private string _status = string.Empty;

    public TagTranslationService(AppPaths paths, AppSettings settings, EhClient client, LogService log)
    {
        _paths = paths;
        _settings = settings;
        _client = client;
        _log = log;
    }

    /// <summary>Raised after a successful update or a failed attempt (status text).</summary>
    public event EventHandler<string>? Changed;

    public bool IsLoaded
    {
        get
        {
            EnsureLoaded();
            return _database is not null;
        }
    }

    public int Count
    {
        get
        {
            EnsureLoaded();
            return _database?.Count ?? 0;
        }
    }

    public string StatusText
    {
        get
        {
            EnsureLoaded();
            if (_status.Length > 0) return _status;
            if (_database is null) return "未下载标签翻译库";
            string updated = DateTime.TryParse(_database.UpdatedUtc, out DateTime value)
                ? value.ToLocalTime().ToString("yyyy-MM-dd")
                : "—";
            return $"{_database.Count:N0} 条 · v{_database.Version} · {updated}";
        }
    }

    public TagTranslationDatabase? Database
    {
        get
        {
            EnsureLoaded();
            return _database;
        }
    }

    /// <summary>Display name for a tag, or null when no translation exists.</summary>
    public string? Translate(string ns, string name)
    {
        if (!_settings.Online.ShowTagTranslations) return null;
        EnsureLoaded();
        return _database?.Translate(ns, name);
    }

    public string Namespace(string ns)
    {
        EnsureLoaded();
        return _database?.NamespaceName(ns) ?? ns;
    }

    /// <summary>
    /// Downloads and installs the database. The release asset is gzip-compressed
    /// JSON; a plain JSON payload is accepted too so the URL stays configurable.
    /// </summary>
    public async Task<bool> UpdateAsync(string? url, CancellationToken cancellationToken)
    {
        if (_updating) return false;
        _updating = true;
        SetStatus("正在下载标签翻译库…");

        try
        {
            string target = string.IsNullOrWhiteSpace(url) ? _settings.Online.TagTranslationUrl : url.Trim();
            byte[] bytes = await _client.DownloadExternalAsync(target, cancellationToken).ConfigureAwait(false);
            string json = Decode(bytes, target);

            TagTranslationDatabase database = TagTranslationParser.Parse(json, VersionFromUrl(target));
            if (database.Count == 0)
            {
                SetStatus("标签翻译库为空，已保留旧数据");
                return false;
            }

            lock (_gate)
            {
                database.Namespaces = new Dictionary<string, string>(database.Namespaces, StringComparer.OrdinalIgnoreCase);
                database.Tags = new Dictionary<string, string>(database.Tags, StringComparer.OrdinalIgnoreCase);
                _database = database;
                _loadAttempted = true;
            }

            if (!JsonFileStore.Save(_paths.TagTranslationFile, database,
                    StorageJsonContext.Default.TagTranslationDatabase, out string? error))
            {
                _log.Warn("Tag translation save failed: " + error);
            }

            _settings.Online.TagTranslationVersion = database.Version;
            _settings.Online.TagTranslationUpdatedUtc = database.UpdatedUtc;
            _settings.Online.TagTranslationCount = database.Count;
            _settings.Online.TagTranslationUrl = target;

            string status = $"已更新 {database.Count:N0} 条（v{database.Version}）";
            SetStatus(status);
            return true;
        }
        catch (Exception ex) when (ex is EhException or HttpRequestException or IOException or InvalidDataException or
                                       System.Text.Json.JsonException or TaskCanceledException)
        {
            _log.Warn("Tag translation update failed: " + ex.Message);
            SetStatus("更新失败：" + ex.Message);
            return false;
        }
        finally
        {
            _updating = false;
        }
    }

    private static string Decode(byte[] bytes, string url)
    {
        bool gzip = bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B;
        if (!gzip && url.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            gzip = true;
        }

        if (!gzip) return System.Text.Encoding.UTF8.GetString(bytes);

        using var input = new MemoryStream(bytes);
        using var decompressor = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressor, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>"…/v7.29720.1/db.text.json.gz" → "7.29720.1" (release tag without the leading v).</summary>
    private static string VersionFromUrl(string url)
    {
        int marker = url.IndexOf("/download/v", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return string.Empty;
        int start = marker + "/download/v".Length;
        int end = url.IndexOf('/', start);
        return end > start ? url[start..end] : url[start..];
    }

    private void EnsureLoaded()
    {
        if (_loadAttempted) return;
        _loadAttempted = true;

        try
        {
            if (!File.Exists(_paths.TagTranslationFile)) return;

            TagTranslationDatabase database = JsonFileStore.LoadOrDefault(
                _paths.TagTranslationFile,
                StorageJsonContext.Default.TagTranslationDatabase,
                () => new TagTranslationDatabase(),
                out string? error);

            if (error is not null)
            {
                _log.Warn("Tag translation load failed: " + error);
                return;
            }

            if (database.Count == 0) return;
            database.Namespaces = new Dictionary<string, string>(database.Namespaces, StringComparer.OrdinalIgnoreCase);
            database.Tags = new Dictionary<string, string>(database.Tags, StringComparer.OrdinalIgnoreCase);
            _database = database;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _log.Warn("Tag translation load failed: " + ex.Message);
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        Changed?.Invoke(this, status);
    }
}
