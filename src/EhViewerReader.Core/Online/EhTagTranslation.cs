using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EhViewerReader.Core.Online;

/// <summary>
/// Translated tag names, built from the community EhTagTranslation database.
/// The file is a plain JSON document:
/// <code>
/// { "version": 7,
///   "data": [ { "namespace": "female", "data": { "sole female": { "name": "仅女性" } } } ] }
/// </code>
/// The namespace <c>rows</c> holds the translated namespace names themselves.
/// </summary>
public sealed class TagTranslationDatabase
{
    public string Version { get; set; } = string.Empty;
    public string UpdatedUtc { get; set; } = string.Empty;

    /// <summary>Site namespace → translated namespace ("female" → "女性").</summary>
    public Dictionary<string, string> Namespaces { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"namespace:tag" → translated tag name.</summary>
    public Dictionary<string, string> Tags { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public int Count => Tags.Count;

    public string? Translate(string ns, string name)
    {
        if (name.Length == 0) return null;
        string key = ns.Length == 0 ? name : ns + ":" + name;
        return Tags.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;
    }

    public string NamespaceName(string ns) =>
        Namespaces.TryGetValue(ns, out string? value) && value.Length > 0 ? value : ns;
}

public static class TagTranslationParser
{
    /// <summary>Parses the full EhTagTranslation JSON document.</summary>
    public static TagTranslationDatabase Parse(string json, string? version = null)
    {
        var database = new TagTranslationDatabase
        {
            UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("version", out JsonElement versionElement))
        {
            database.Version = versionElement.ValueKind switch
            {
                JsonValueKind.String => versionElement.GetString() ?? string.Empty,
                JsonValueKind.Number => versionElement.ToString(),
                _ => database.Version,
            };
        }

        if (version is { Length: > 0 }) database.Version = version;
        if (!root.TryGetProperty("data", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return database;
        }

        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("namespace", out JsonElement namespaceElement)) continue;
            string ns = namespaceElement.GetString() ?? string.Empty;
            if (ns.Length == 0) continue;

            if (!group.TryGetProperty("data", out JsonElement entries) ||
                entries.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool isNamespaceRow = string.Equals(ns, "rows", StringComparison.OrdinalIgnoreCase);
            foreach (JsonProperty entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!entry.Value.TryGetProperty("name", out JsonElement nameElement)) continue;

                string translated = nameElement.GetString() ?? string.Empty;
                if (translated.Length == 0) continue;

                if (isNamespaceRow) database.Namespaces[entry.Name] = translated;
                else database.Tags[ns + ":" + entry.Name] = translated;
            }
        }

        return database;
    }
}
