using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace EhViewerForPc.Services;

/// <summary>
/// Writes the diagnostic reports emitted by <c>--self-test</c> / <c>--ui-smoke</c>.
/// The reports are ad-hoc dictionaries with anonymous-object values, which no
/// source-generated context can describe. On NativeAOT (where reflection-based
/// serialization does not exist) the writer degrades to a flat JSON document
/// built with <see cref="Utf8JsonWriter"/>; the rich form is only produced when
/// dynamic code is available.
/// </summary>
internal static class DiagnosticReport
{
    public static async Task WriteAsync(string path, IReadOnlyDictionary<string, object?> report)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = RuntimeFeature.IsDynamicCodeSupported
            ? SerializeRich(report)
            : SerializeFlat(report);

        await File.WriteAllTextAsync(path, json);
    }

    [UnconditionalSuppressMessage(
        "Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Diagnostic-only path, unreachable under NativeAOT.")]
    [UnconditionalSuppressMessage(
        "AOT", "IL3050:RequiresDynamicCode",
        Justification = "Diagnostic-only path, unreachable under NativeAOT.")]
    private static string SerializeRich(IReadOnlyDictionary<string, object?> report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

    private static string SerializeFlat(IReadOnlyDictionary<string, object?> report)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteObject(writer, report);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteObject(Utf8JsonWriter writer, IReadOnlyDictionary<string, object?> report)
    {
        writer.WriteStartObject();
        foreach ((string key, object? value) in report)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case IReadOnlyDictionary<string, object?> nested:
                WriteObject(writer, nested);
                break;
            case System.Collections.IEnumerable items:
                writer.WriteStartArray();
                foreach (object? item in items) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
