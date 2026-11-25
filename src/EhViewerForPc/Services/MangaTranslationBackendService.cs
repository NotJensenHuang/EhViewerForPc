using System.Security.Cryptography;
using System.Text;
using EhViewerForPc.Core.Storage;

namespace EhViewerForPc.Services;

internal enum MangaTranslationInstallStage
{
    Preparing,
    Connecting,
    Downloading,
    Retrying,
    Copying,
    Verifying,
    Completed,
}

internal sealed record MangaTranslationInstallProgress(string Message, double Fraction,
    MangaTranslationInstallStage Stage = MangaTranslationInstallStage.Preparing, string? FileName = null,
    long BytesReceived = 0, long TotalBytes = 0, string? SourceHost = null);

internal sealed record MangaTranslationBackendPaths(string RuntimeDirectory, string ModelDirectory, MangaTranslationModelOption Model, string ModelSignature);

internal sealed class MangaTranslationException(string message, bool requiresSetup = false) : Exception(message)
{
    public bool RequiresSetup { get; } = requiresSetup;
}

internal sealed class MangaTranslationBackendService(AppPaths paths) : IDisposable
{
    internal const string MissingNativeLibrariesMessage = "此发行包缺少本地 GPU 翻译库。请重新安装或解压完整的 Windows x64 发行包。";

    // 沿用旧版已校验模型记录的键，模型本身的字节和签名格式没有变化。
    private const string ModelReceiptVersion = "runtime-v1-";
    private const string OverrideVariable = "EHVIEWER_MANGA_TRANSLATION_BACKEND";
    private const string CacheVariable = "EHVIEWER_MANGA_TRANSLATION_ASSET_CACHE";
    private static readonly string[] NativeLibraries =
    [
        "ort/onnxruntime.dll", "ort/DirectML.dll", "ort/onnxruntime_providers_shared.dll",
        "gguf/llama.dll", "gguf/ggml.dll", "gguf/ggml-base.dll", "gguf/ggml-vulkan.dll",
        "gguf/ggml-cpu-x64.dll", "gguf/libomp.dll",
        "ort/msvcp140.dll", "ort/msvcp140_1.dll", "ort/vcruntime140.dll", "ort/vcruntime140_1.dll",
        "gguf/msvcp140.dll", "gguf/msvcp140_1.dll", "gguf/vcruntime140.dll", "gguf/vcruntime140_1.dll",
    ];

    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task _install = Task.CompletedTask;
    private bool _disposed;

    private string LegacyDirectory => Path.Combine(paths.MangaTranslationDirectory, "backend");

    public string DefaultModelDirectory => Path.Combine(LegacyDirectory, "models");

    internal static string NativeDirectory => Path.Combine(AppContext.BaseDirectory, "manga-native");

    public string GetModelDirectory(string? configured)
    {
        string? external = GetLegacyOverride(configured);
        return NormalizeLocalDirectory(external is not null ? Path.Combine(external, "models") :
            string.IsNullOrWhiteSpace(configured) ? DefaultModelDirectory : configured,
            "请选择本机磁盘上的实际模型目录，目录应使用完整路径且不能经过链接或联接。");
    }

    public bool IsReady(string? configured, MangaTranslationModelOption? model = null)
    {
        try { return Resolve(configured, model) is not null; }
        catch (MangaTranslationException) { return false; }
    }

    public Task InstallAsync(string? configured, IProgress<MangaTranslationInstallProgress>? progress, CancellationToken token, MangaTranslationModelOption? model = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_install.IsCompleted) throw new MangaTranslationException("本地翻译资源正在下载。");
            _install = Task.Run(async () =>
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
                await InstallCoreAsync(configured, progress, cancellation.Token, model ?? MangaTranslationModelOption.Resolve(null)).ConfigureAwait(false);
            });
            return _install;
        }
    }

    internal MangaTranslationBackendPaths? Resolve(string? configured, MangaTranslationModelOption? model = null)
    {
        model ??= MangaTranslationModelOption.Resolve(null);
        string models = GetModelDirectory(configured);
        string? signature = MangaTranslationAssetInstaller.GetModelSignature(models, model);
        return signature is not null && HasModelReceipt(models, signature, model) && HasNativeLibraries()
            ? new MangaTranslationBackendPaths(NativeDirectory, models, model, signature) : null;
    }

    private async Task InstallCoreAsync(string? configured, IProgress<MangaTranslationInstallProgress>? progress,
        CancellationToken token, MangaTranslationModelOption model)
    {
        token.ThrowIfCancellationRequested();
        if (!HasNativeLibraries())
            throw new MangaTranslationException(MissingNativeLibrariesMessage);

        string models = GetModelDirectory(configured);
        string root = NormalizeLocalDirectory(paths.MangaTranslationDirectory, "应用数据目录必须位于本机磁盘上的实际目录。");
        string? configuredCache = Environment.GetEnvironmentVariable(CacheVariable);
        string? cache = string.IsNullOrWhiteSpace(configuredCache) ? null
            : NormalizeLocalDirectory(configuredCache, "离线资源缓存路径无效。");
        string id = Guid.NewGuid().ToString("N")[..16];
        string staging = Path.Combine(root, ".install-" + id);

        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(models);
            string? signature = MangaTranslationAssetInstaller.GetModelSignature(models, model);
            if (signature is null || !HasModelReceipt(models, signature, model))
            {
                using var installer = new MangaTranslationAssetInstaller(cache, progress);
                signature = await installer.InstallAsync(models, token, model).ConfigureAwait(false);
                await WriteModelReceiptAsync(models, signature, staging, token, model).ConfigureAwait(false);
            }
            progress?.Report(new MangaTranslationInstallProgress("本地翻译模型已就绪。", 1, MangaTranslationInstallStage.Completed));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (MangaTranslationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException
                                   or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            throw new MangaTranslationException("本地翻译资源安装失败。请检查网络、目录权限和剩余空间后重试。");
        }
        finally
        {
            // 仅清理本次安装独占的暂存目录，保留已经验明并完成提交的模型。
            if (PathIdentity.Comparer.Equals(Path.GetDirectoryName(staging), root) && IsLocalDirectory(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private string ModelReceiptPath(string modelDirectory, MangaTranslationModelOption model)
    {
        string pathKey = OperatingSystem.IsWindows() ? modelDirectory.ToUpperInvariant() : modelDirectory;
        string version = model == MangaTranslationModelOption.Choices[0]
            ? ModelReceiptVersion : ModelReceiptVersion + model.Id + "|";
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(version + pathKey));
        return Path.Combine(paths.MangaTranslationDirectory, "verified-models", Convert.ToHexString(key) + ".txt");
    }

    private bool HasModelReceipt(string models, string signature, MangaTranslationModelOption model)
    {
        string receipt = ModelReceiptPath(models, model);
        if (!IsLocalFile(receipt, Encoding.UTF8.GetByteCount(signature))) return false;
        try { return File.ReadAllText(receipt) == signature; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private async Task WriteModelReceiptAsync(string models, string signature, string staging, CancellationToken token, MangaTranslationModelOption model)
    {
        if (MangaTranslationAssetInstaller.GetModelSignature(models, model) != signature)
            throw new MangaTranslationException("模型文件在验证后发生变化。请关闭可能占用该目录的程序，再重试下载。");

        string receipt = ModelReceiptPath(models, model);
        string directory = Path.GetDirectoryName(receipt)!;
        if (!IsLocalDirectory(directory)) throw new IOException("本地模型校验记录目录无效。");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(staging, "model-receipt.txt");
        await File.WriteAllTextAsync(temporary, signature, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        File.Move(temporary, receipt, overwrite: true);
    }

    internal static bool HasNativeLibraries() => NativeLibraries.All(name => File.Exists(Path.Combine(NativeDirectory, name)));

    private static string? GetLegacyOverride(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return null;
        string? path = Environment.GetEnvironmentVariable(OverrideVariable);
        return string.IsNullOrWhiteSpace(path) ? null : NormalizeLocalDirectory(path,
            "本地漫画翻译后端路径需要指向本机磁盘上的实际目录。");
    }

    internal static bool IsLocalFile(string path, long? length = null)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) is not { } parent || !IsLocalDirectory(parent)) return false;
            var file = new FileInfo(full);
            return file.Exists && (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                (length is null || file.Length == length);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { return false; }
    }

    private static string NormalizeLocalDirectory(string path, string error)
    {
        try
        {
            if (Path.IsPathFullyQualified(path))
            {
                string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (IsLocalDirectory(full)) return full;
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException) { }
        throw new MangaTranslationException(error);
    }

    internal static bool IsLocalDirectory(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (root is null) return false;
        if (OperatingSystem.IsWindows() &&
            (root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)))
            return false;

        string current = root;
        foreach (string segment in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment.EndsWith(' ') || segment.EndsWith('.')) return false;
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    return false;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    public void Dispose()
    {
        Task pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _install;
        }
        _lifetime.Cancel();
        try { pending.GetAwaiter().GetResult(); }
        catch (Exception) when (pending.IsFaulted || pending.IsCanceled) { }
        finally { _lifetime.Dispose(); }
    }
}
