using System.Text.Json.Serialization;

namespace EhViewerReader.ImageSearch;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FeatureProfile))]
[JsonSerializable(typeof(ImportEntry))]
[JsonSerializable(typeof(ImportEntry[]))]
[JsonSerializable(typeof(SearchHit[]))]
[JsonSerializable(typeof(ImportSummary))]
[JsonSerializable(typeof(BuildResult))]
internal partial class SearchJsonContext : JsonSerializerContext;
