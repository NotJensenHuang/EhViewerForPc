using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewerReader.Core.Download;
using EhViewerReader.Core.Models;
using EhViewerReader.Core.Online;

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
[JsonSerializable(typeof(WorkCatalogFile))]
[JsonSerializable(typeof(LibraryCache))]
[JsonSerializable(typeof(DownloadFile))]
[JsonSerializable(typeof(DownloadRootMoveJournal))]
[JsonSerializable(typeof(TagTranslationDatabase))]
public sealed partial class StorageJsonContext : JsonSerializerContext;
