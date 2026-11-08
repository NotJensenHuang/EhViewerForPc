using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewerReader.Core.Models;

namespace EhViewerReader.Core.Storage;

/// <summary>
/// Source-generated serializer metadata for every persisted document.
/// NativeAOT builds have no reflection-based serializer, so each root type must
/// be registered here; nested types are generated transitively.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ProgressFile))]
[JsonSerializable(typeof(LibraryCache))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
