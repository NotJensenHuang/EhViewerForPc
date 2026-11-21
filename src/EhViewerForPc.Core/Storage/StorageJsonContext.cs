using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewerForPc.Core.Download;
using EhViewerForPc.Core.Models;
using EhViewerForPc.Core.Online;

namespace EhViewerForPc.Core.Storage;

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
[JsonSerializable(typeof(ImageIndexBuildCheckpoint))]
[JsonSerializable(typeof(UpstreamHostsCache))]
public sealed partial class StorageJsonContext : JsonSerializerContext;
