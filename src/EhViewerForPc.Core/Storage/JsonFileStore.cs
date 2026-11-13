using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace EhViewerForPc.Core.Storage;

/// <summary>
/// File-backed JSON helpers. Every entry point takes a <see cref="JsonTypeInfo{T}"/>
/// produced by a source-generated context so the code is safe under NativeAOT
/// (no reflection-based serialization anywhere on the runtime path).
/// </summary>
public static class JsonFileStore
{
    public static T LoadOrDefault<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> factory, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path)) return factory();
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return factory();
            T? value = JsonSerializer.Deserialize(json, typeInfo);
            if (value is null)
            {
                error = "JSON root is null";
                return factory();
            }

            return value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            TryMoveCorruptFile(path);
            return factory();
        }
    }

    public static bool Save<T>(string path, T value, JsonTypeInfo<T> typeInfo, out string? error)
    {
        error = null;
        string? tempPath = null;
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                       FileOptions.SequentialScan))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, value, typeInfo);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
            tempPath = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }
    }

    private static void TryMoveCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            string backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(path, backup, overwrite: false);
        }
        catch
        {
            // Best effort; the caller falls back to defaults anyway.
        }
    }
}
