using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EhViewerForPc.Services;

// 绑定打包的 llama.cpp b12650 Windows x64 C ABI。其他构建可能改变按值传递的结构体。
internal sealed unsafe partial class HyMtLlamaNative
{
    private const uint SearchDllLoadDirectory = 0x00000100;
    private const uint SearchSystem32 = 0x00000800;
    private const string GpuUnavailable = "没有可用的 Vulkan GPU。请检查显卡和 Vulkan 驱动后重试。";
    private const string CpuBackend = "ggml-cpu-x64.dll";
    private const string VulkanBackend = "ggml-vulkan.dll";
    private static readonly object InitializationGate = new();
    private static Lazy<HyMtLlamaNative>? _instance;
    private static string? _directory;

    private readonly nint _llama;
    private readonly nint _ggml;
    private readonly nint _ggmlBase;

    internal static HyMtLlamaNative Get(string runtimeDirectory)
    {
        string directory = Path.GetFullPath(Path.Combine(runtimeDirectory, "gguf"));
        Lazy<HyMtLlamaNative> instance;
        lock (InitializationGate)
        {
            if (_directory is not null && !string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase))
                throw new MangaTranslationException("本地翻译原生运行时目录已变化，请重启应用。", requiresSetup: true);
            _directory ??= directory;
            instance = _instance ??= new Lazy<HyMtLlamaNative>(() => new HyMtLlamaNative(directory));
        }
        return instance.Value;
    }

    private HyMtLlamaNative(string directory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new MangaTranslationException("本地漫画翻译需要 Windows x64。", requiresSetup: true);

        try
        {
            // ggml 全局插件注册表持有模块中的指针，模块随进程释放。
            // llama_version 只返回 1.0.0-dev，通过发行文件的 SHA-256 锁定 ABI。
            LoadApprovedModule(directory, "libomp.dll", "1E4222F195D84E7355109D778E751DFE3C085E42C947F7FE3D7853181329DD40");
            _ggmlBase = LoadApprovedModule(directory, "ggml-base.dll", "3B40939B79E2BA84F461BE056096E25D60AAB3934FA6AA5B8AFBEB0B53738DAE");
            _ggml = LoadApprovedModule(directory, "ggml.dll", "D118CC2158498F778671F5878B6233EDC2F7ED16A9827AD820264841E30331E9");
            _llama = LoadApprovedModule(directory, "llama.dll", "AE7CBC2D161B47A29D15C5B9CA2B0C157629173ED7CED00F4B301E2B5003491D");

            var loadBackend = (delegate* unmanaged[Cdecl]<byte*, nint>)Export(_ggml, "ggml_backend_load");
            LoadApprovedModule(directory, CpuBackend, "A7551A9DCE5E64D1BC5EE37E452169E30D16A06EE27572024453C28AB4C1181D");
            if (LoadBackend(loadBackend, directory, CpuBackend) == 0)
                throw new MangaTranslationException("无法加载本地翻译的基础计算组件。", requiresSetup: true);

            LoadApprovedModule(directory, VulkanBackend, "F1B8E7177C1552A5B616A51B98463CABB8BFD1E18AFB4E4E9F7200F8D1CCA5CA", gpu: true);
            nint vulkan = LoadBackend(loadBackend, directory, VulkanBackend);
            if (vulkan == 0) throw new MangaTranslationException(GpuUnavailable);
            Device = SelectVulkanDevice(vulkan);
            DeviceName = Marshal.PtrToStringUTF8(((delegate* unmanaged[Cdecl]<nint, nint>)
                Export(_ggmlBase, "ggml_backend_dev_name"))(Device)) ?? "Vulkan";
            ((delegate* unmanaged[Cdecl]<void>)Export(_llama, "llama_backend_init"))();

            DefaultModelParameters = (delegate* unmanaged[Cdecl]<ModelParameters>)Export(_llama, "llama_model_default_params");
            DefaultContextParameters = (delegate* unmanaged[Cdecl]<ContextParameters>)Export(_llama, "llama_context_default_params");
            ModelLoad = (delegate* unmanaged[Cdecl]<byte*, ModelParameters, nint>)Export(_llama, "llama_model_load_from_file");
            ModelFree = (delegate* unmanaged[Cdecl]<nint, void>)Export(_llama, "llama_model_free");
            ChatTemplate = (delegate* unmanaged[Cdecl]<nint, nint, nint>)Export(_llama, "llama_model_chat_template");
            GetVocabulary = (delegate* unmanaged[Cdecl]<nint, nint>)Export(_llama, "llama_model_get_vocab");
            GetBos = (delegate* unmanaged[Cdecl]<nint, int>)Export(_llama, "llama_vocab_bos");
            GetVocabularySize = (delegate* unmanaged[Cdecl]<nint, int>)Export(_llama, "llama_vocab_n_tokens");
            SetLog = (delegate* unmanaged[Cdecl]<nint, nint, void>)Export(_llama, "llama_log_set");
            GetLog = (delegate* unmanaged[Cdecl]<nint*, nint*, void>)Export(_llama, "llama_log_get");
            ContextCreate = (delegate* unmanaged[Cdecl]<nint, ContextParameters, nint>)Export(_llama, "llama_init_from_model");
            ContextFree = (delegate* unmanaged[Cdecl]<nint, void>)Export(_llama, "llama_free");
            GetContextSize = (delegate* unmanaged[Cdecl]<nint, uint>)Export(_llama, "llama_n_ctx_seq");
            GetBatchSize = (delegate* unmanaged[Cdecl]<nint, uint>)Export(_llama, "llama_n_batch");
            GetMemory = (delegate* unmanaged[Cdecl]<nint, nint>)Export(_llama, "llama_get_memory");
            ClearMemory = (delegate* unmanaged[Cdecl]<nint, byte, void>)Export(_llama, "llama_memory_clear");
            Synchronize = (delegate* unmanaged[Cdecl]<nint, void>)Export(_llama, "llama_synchronize");
            BatchGetOne = (delegate* unmanaged[Cdecl]<int*, int, Batch>)Export(_llama, "llama_batch_get_one");
            Decode = (delegate* unmanaged[Cdecl]<nint, Batch, int>)Export(_llama, "llama_decode");
            Tokenize = (delegate* unmanaged[Cdecl]<nint, byte*, int, int*, int, byte, byte, int>)Export(_llama, "llama_tokenize");
            Detokenize = (delegate* unmanaged[Cdecl]<nint, int*, int, byte*, int, byte, byte, int>)Export(_llama, "llama_detokenize");
            IsEndOfGeneration = (delegate* unmanaged[Cdecl]<nint, int, byte>)Export(_llama, "llama_vocab_is_eog");
            SamplerChainCreate = (delegate* unmanaged[Cdecl]<SamplerParameters, nint>)Export(_llama, "llama_sampler_chain_init");
            SamplerChainAdd = (delegate* unmanaged[Cdecl]<nint, nint, void>)Export(_llama, "llama_sampler_chain_add");
            SamplerPenalties = (delegate* unmanaged[Cdecl]<int, int, float, float, float, nint>)Export(_llama, "llama_sampler_init_penalties");
            SamplerTopK = (delegate* unmanaged[Cdecl]<int, nint>)Export(_llama, "llama_sampler_init_top_k");
            SamplerTopP = (delegate* unmanaged[Cdecl]<float, nuint, nint>)Export(_llama, "llama_sampler_init_top_p");
            SamplerTemperature = (delegate* unmanaged[Cdecl]<float, nint>)Export(_llama, "llama_sampler_init_temp");
            SamplerDistribution = (delegate* unmanaged[Cdecl]<uint, nint>)Export(_llama, "llama_sampler_init_dist");
            SamplerSample = (delegate* unmanaged[Cdecl]<nint, nint, int, int>)Export(_llama, "llama_sampler_sample");
            SamplerFree = (delegate* unmanaged[Cdecl]<nint, void>)Export(_llama, "llama_sampler_free");
        }
        catch (EntryPointNotFoundException)
        {
            throw new MangaTranslationException("本地翻译原生运行时接口不完整，请重新获取应用。", requiresSetup: true);
        }
    }

    private nint SelectVulkanDevice(nint registration)
    {
        var getCount = (delegate* unmanaged[Cdecl]<nint, nuint>)Export(_ggmlBase, "ggml_backend_reg_dev_count");
        var getDevice = (delegate* unmanaged[Cdecl]<nint, nuint, nint>)Export(_ggmlBase, "ggml_backend_reg_dev_get");
        var getType = (delegate* unmanaged[Cdecl]<nint, int>)Export(_ggmlBase, "ggml_backend_dev_type");
        nint integratedGpu = 0;
        for (nuint i = 0, count = getCount(registration); i < count; i++)
        {
            nint device = getDevice(registration, i);
            int type = getType(device);
            if (type == 1) return device; // GGML_BACKEND_DEVICE_TYPE_GPU
            if (type == 2 && integratedGpu == 0) integratedGpu = device; // GGML_BACKEND_DEVICE_TYPE_IGPU
        }
        return integratedGpu != 0 ? integratedGpu : throw new MangaTranslationException(GpuUnavailable);
    }

    private static nint LoadBackend(delegate* unmanaged[Cdecl]<byte*, nint> load, string directory, string name)
    {
        byte[] path = Encoding.UTF8.GetBytes(Path.Combine(directory, name) + '\0');
        fixed (byte* nativePath = path) return load(nativePath);
    }

    private static nint LoadApprovedModule(string directory, string name, string expectedHash, bool gpu = false)
    {
        string path = Path.Combine(directory, name);
        if (!MangaTranslationBackendService.IsLocalFile(path))
            throw new MangaTranslationException("本地翻译原生文件不完整，请重新获取应用。", requiresSetup: true);

        // 在 ggml 按绝对路径注册之前，先以受限 DLL 搜索路径加载已校验的模块。
        nint existing = GetModuleHandle(name);
        if (existing != 0)
        {
            Span<char> buffer = stackalloc char[1024];
            fixed (char* pointer = buffer)
            {
                uint length = GetModuleFileName(existing, pointer, (uint)buffer.Length);
                if (length == 0 || length >= buffer.Length ||
                    !buffer[..(int)length].Equals(path.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    throw new MangaTranslationException("本地翻译原生组件与已加载的同名组件冲突。", requiresSetup: true);
            }
        }
        nint module;
        try
        {
            using FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (Convert.ToHexString(SHA256.HashData(file)) != expectedHash)
                throw new MangaTranslationException("本地翻译原生组件校验失败，请重新获取应用。", requiresSetup: true);
            module = LoadLibraryEx(path, 0, SearchDllLoadDirectory | SearchSystem32);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MangaTranslationException("无法读取本地翻译原生组件，请重新获取应用。", requiresSetup: true);
        }
        if (module == 0)
            throw new MangaTranslationException(gpu ? GpuUnavailable : "无法加载本地翻译原生组件，请重新获取应用。",
                requiresSetup: !gpu);
        return module;
    }

    private static nint Export(nint library, string name) => NativeLibrary.GetExport(library, name);

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadLibraryEx(string fileName, nint file, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetModuleHandle(string name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetModuleFileName(nint module, char* buffer, uint length);

    internal nint Device { get; }
    internal string DeviceName { get; }
    internal object ModelLoadGate { get; } = new();
    internal readonly delegate* unmanaged[Cdecl]<ModelParameters> DefaultModelParameters;
    internal readonly delegate* unmanaged[Cdecl]<ContextParameters> DefaultContextParameters;
    internal readonly delegate* unmanaged[Cdecl]<byte*, ModelParameters, nint> ModelLoad;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> ModelFree;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, nint> ChatTemplate;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint> GetVocabulary;
    internal readonly delegate* unmanaged[Cdecl]<nint, int> GetBos;
    internal readonly delegate* unmanaged[Cdecl]<nint, int> GetVocabularySize;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, void> SetLog;
    internal readonly delegate* unmanaged[Cdecl]<nint*, nint*, void> GetLog;
    internal readonly delegate* unmanaged[Cdecl]<nint, ContextParameters, nint> ContextCreate;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> ContextFree;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> GetContextSize;
    internal readonly delegate* unmanaged[Cdecl]<nint, uint> GetBatchSize;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint> GetMemory;
    internal readonly delegate* unmanaged[Cdecl]<nint, byte, void> ClearMemory;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> Synchronize;
    internal readonly delegate* unmanaged[Cdecl]<int*, int, Batch> BatchGetOne;
    internal readonly delegate* unmanaged[Cdecl]<nint, Batch, int> Decode;
    internal readonly delegate* unmanaged[Cdecl]<nint, byte*, int, int*, int, byte, byte, int> Tokenize;
    internal readonly delegate* unmanaged[Cdecl]<nint, int*, int, byte*, int, byte, byte, int> Detokenize;
    internal readonly delegate* unmanaged[Cdecl]<nint, int, byte> IsEndOfGeneration;
    internal readonly delegate* unmanaged[Cdecl]<SamplerParameters, nint> SamplerChainCreate;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, void> SamplerChainAdd;
    internal readonly delegate* unmanaged[Cdecl]<int, int, float, float, float, nint> SamplerPenalties;
    internal readonly delegate* unmanaged[Cdecl]<int, nint> SamplerTopK;
    internal readonly delegate* unmanaged[Cdecl]<float, nuint, nint> SamplerTopP;
    internal readonly delegate* unmanaged[Cdecl]<float, nint> SamplerTemperature;
    internal readonly delegate* unmanaged[Cdecl]<uint, nint> SamplerDistribution;
    internal readonly delegate* unmanaged[Cdecl]<nint, nint, int, int> SamplerSample;
    internal readonly delegate* unmanaged[Cdecl]<nint, void> SamplerFree;

    // 原生默认值初始化整个结构体，未覆写的字段按原值传回原生接口。
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct ModelParameters
    {
        [FieldOffset(0)] internal nint Devices;
        [FieldOffset(16)] internal int GpuLayers;
        [FieldOffset(48)] internal nint ProgressCallback;
        [FieldOffset(56)] internal nint ProgressData;
    }

    [StructLayout(LayoutKind.Explicit, Size = 176)]
    internal struct ContextParameters
    {
        [FieldOffset(0)] internal uint ContextSize;
        [FieldOffset(4)] internal uint BatchSize;
        [FieldOffset(8)] internal uint MicroBatchSize;
        [FieldOffset(12)] internal uint SequenceCount;
        [FieldOffset(28)] internal int Threads;
        [FieldOffset(32)] internal int BatchThreads;
    }

    [StructLayout(LayoutKind.Explicit, Size = 56)]
    internal struct Batch { }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SamplerParameters { internal byte NoPerf; }
}
