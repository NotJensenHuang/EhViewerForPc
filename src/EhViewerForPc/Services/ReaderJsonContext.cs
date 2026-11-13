using System.Text.Json;
using System.Text.Json.Serialization;

namespace EhViewerForPc.Services;

/// <summary>
/// Source-generated serializer metadata for view-layer documents. Kept next to
/// the types it covers so NativeAOT builds never fall back to reflection.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(PageDimensionCacheFile))]
[JsonSerializable(typeof(UiAuditReport))]
internal sealed partial class ReaderJsonContext : JsonSerializerContext;
