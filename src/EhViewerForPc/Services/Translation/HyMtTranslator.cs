using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EhViewerForPc.Services;

// 同一页复用模型及上下文，组间重置提示与 KV 状态。
internal sealed unsafe class HyMtTranslator : IDisposable
{
    private const string ModelName = "Hy-MT2-1.8B-Q4_K_M.gguf";
    private const long ModelBytes = 1_133_080_448;
    private const string ChatTemplateHash = "B7491EC0E9C869DFCE20F2176758099BF248D979DD05530EDE99DEB21698ACEE";
    private const string Instruction = "将以下日语漫画对白忠实翻译为简体中文，保留语气、措辞和标点。只输出译文，不要额外解释。\n\n";
    private const int ContextTokens = 2048;
    private const int BatchTokens = 128;
    private const int OutputTokens = 256;
    private const int OutputBytes = 16 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HyMtLlamaNative _native;
    private nint _model;
    private nint _context;
    private nint _vocabulary;
    private nint _memory;
    private nint* _devices;
    private FileStream? _modelFile;
    private GCHandle _loadStateHandle;
    private int _bos;
    private int _user;
    private int _assistant;
    private int _batchSize;
    private int _promptCapacity;
    private bool _disposed;

    private HyMtTranslator(HyMtLlamaNative native) => _native = native;

    internal string Device => _native.DeviceName;
    internal int OffloadedLayers { get; private set; }

    internal static HyMtTranslator Load(string runtimeDirectory, string modelDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string modelPath = Path.Combine(modelDirectory, ModelName);
        if (!MangaTranslationBackendService.IsLocalFile(modelPath, ModelBytes))
            throw new MangaTranslationException("本地翻译模型未准备完整。", requiresSetup: true);

        var translator = new HyMtTranslator(HyMtLlamaNative.Get(runtimeDirectory));
        try
        {
            translator.LoadModel(modelPath, token);
            return translator;
        }
        catch
        {
            translator.Dispose();
            throw;
        }
    }

    private void LoadModel(string modelPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { _modelFile = File.Open(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MangaTranslationException("无法读取本地翻译模型，请检查模型目录。", requiresSetup: true);
        }
        if (_modelFile.Length != ModelBytes)
            throw new MangaTranslationException("本地翻译模型文件已变化，请重新检查模型。", requiresSetup: true);

        var state = new ModelLoadState(token);
        _loadStateHandle = GCHandle.Alloc(state);
        _devices = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        _devices[0] = _native.Device;
        HyMtLlamaNative.ModelParameters modelParameters = _native.DefaultModelParameters();
        modelParameters.Devices = (nint)_devices;
        modelParameters.GpuLayers = -1; // 卸载所选 Vulkan 设备支持的全部层。
        modelParameters.ProgressCallback = (nint)(delegate* unmanaged[Cdecl]<float, nint, byte>)&CheckModelProgress;
        modelParameters.ProgressData = GCHandle.ToIntPtr(_loadStateHandle);
        byte[] path = Encoding.UTF8.GetBytes(modelPath + '\0');
        lock (_native.ModelLoadGate)
        {
            token.ThrowIfCancellationRequested();
            nint priorLog;
            nint priorData;
            _native.GetLog(&priorLog, &priorData);
            _native.SetLog((nint)(delegate* unmanaged[Cdecl]<int, nint, nint, void>)&ObserveModelLoad, modelParameters.ProgressData);
            try
            {
                fixed (byte* nativePath = path) _model = _native.ModelLoad(nativePath, modelParameters);
            }
            finally { _native.SetLog(priorLog, priorData); }
        }

        token.ThrowIfCancellationRequested();
        if (_model == 0 || state.OffloadedLayers <= 0)
            throw new MangaTranslationException("本地翻译模型未能加载到 Vulkan GPU。请检查显卡驱动和可用显存。");
        OffloadedLayers = state.OffloadedLayers;

        _vocabulary = _native.GetVocabulary(_model);
        if (_vocabulary == 0 || !HasExpectedChatTemplate())
            throw new MangaTranslationException("本地翻译模型的词表或对话模板不兼容。", requiresSetup: true);
        _bos = ReadSpecialToken("<｜hy_begin▁of▁sentence｜>"u8);
        _user = ReadSpecialToken("<｜hy_User｜>"u8);
        _assistant = ReadSpecialToken("<｜hy_Assistant｜>"u8);
        if (_bos != _native.GetBos(_vocabulary))
            throw new MangaTranslationException("本地翻译模型的起始标记不兼容。", requiresSetup: true);

        HyMtLlamaNative.ContextParameters parameters = _native.DefaultContextParameters();
        parameters.ContextSize = ContextTokens;
        parameters.BatchSize = BatchTokens;
        parameters.MicroBatchSize = BatchTokens;
        parameters.SequenceCount = 1;
        parameters.Threads = Math.Min(Environment.ProcessorCount, 4);
        parameters.BatchThreads = parameters.Threads;
        token.ThrowIfCancellationRequested();
        _context = _native.ContextCreate(_model, parameters);
        token.ThrowIfCancellationRequested();
        if (_context == 0)
            throw new MangaTranslationException("无法创建 Vulkan 翻译上下文。请检查可用显存后重试。");
        _memory = _native.GetMemory(_context);
        _batchSize = (int)Math.Min(_native.GetBatchSize(_context), BatchTokens);
        _promptCapacity = (int)Math.Min(_native.GetContextSize(_context), ContextTokens) - OutputTokens;
        if (_memory == 0 || _batchSize < 1 || _promptCapacity < 4)
            throw new MangaTranslationException("本地翻译模型的上下文不可用。");
    }

    private bool HasExpectedChatTemplate()
    {
        string? template = Marshal.PtrToStringUTF8(_native.ChatTemplate(_model, 0));
        return template is not null && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(template))) == ChatTemplateHash;
    }

    private int ReadSpecialToken(ReadOnlySpan<byte> marker)
    {
        int id;
        fixed (byte* input = marker)
            if (_native.Tokenize(_vocabulary, input, marker.Length, &id, 1, 0, 1) == 1) return id;
        throw new MangaTranslationException("本地翻译模型的对话标记不兼容。", requiresSetup: true);
    }

    internal string Translate(string text, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > ContextTokens)
            throw new MangaTranslationException("当前对白为空或超过本地翻译长度上限。");

        int[] prompt = ArrayPool<int>.Shared.Rent(_promptCapacity);
        try
        {
            byte[] input = Encoding.UTF8.GetBytes(Instruction + text);
            Span<int> tokens = prompt.AsSpan(0, _promptCapacity);
            tokens[0] = _bos;
            tokens[1] = _user;
            int count;
            // GGUF 单轮模板依次写入起始标记、用户标记、提示及原文、助手标记。
            // 保持 OCR 文字正文的特殊标记外观，不把它们解析为控制令牌。
            fixed (byte* data = input)
            fixed (int* target = tokens)
                count = _native.Tokenize(_vocabulary, data, input.Length, target + 2, tokens.Length - 3, 0, 0);
            if (count <= 0 || count > tokens.Length - 3)
                throw new MangaTranslationException("当前对白超过本地翻译长度上限。");
            tokens[2 + count] = _assistant;

            _native.ClearMemory(_memory, 0);
            ReadOnlySpan<int> pending = tokens[..(count + 3)];
            while (!pending.IsEmpty)
            {
                int length = Math.Min(pending.Length, _batchSize);
                Decode(pending[..length], token);
                pending = pending[length..];
            }

            return Generate(token);
        }
        finally { ArrayPool<int>.Shared.Return(prompt, clearArray: true); }
    }

    private void Decode(ReadOnlySpan<int> input, CancellationToken token)
    {
        // 原生中断回调只支持 CPU。在每批 Vulkan 提交和每个译文令牌的前后观察取消。
        token.ThrowIfCancellationRequested();
        int result;
        fixed (int* pointer = input) result = _native.Decode(_context, _native.BatchGetOne(pointer, input.Length));
        token.ThrowIfCancellationRequested();
        if (result != 0) throw new MangaTranslationException("Vulkan 本地翻译推理失败。");
    }

    private string Generate(CancellationToken token)
    {
        nint sampler = _native.SamplerChainCreate(new HyMtLlamaNative.SamplerParameters { NoPerf = 1 });
        if (sampler == 0) throw new MangaTranslationException("无法创建本地翻译采样器。");
        try
        {
            AddSampler(sampler, _native.SamplerPenalties(_native.GetVocabularySize(_vocabulary), 64, 1.05f, 0, 0));
            AddSampler(sampler, _native.SamplerTopK(20));
            AddSampler(sampler, _native.SamplerTopP(0.6f, 1));
            AddSampler(sampler, _native.SamplerTemperature(0.15f));
            AddSampler(sampler, _native.SamplerDistribution((uint)RandomNumberGenerator.GetInt32(int.MaxValue)));

            Span<int> output = stackalloc int[OutputTokens];
            for (int count = 0; count < output.Length; count++)
            {
                token.ThrowIfCancellationRequested();
                int id = _native.SamplerSample(sampler, _context, -1);
                if (_native.IsEndOfGeneration(_vocabulary, id) != 0) return DecodeOutput(output[..count]);
                output[count] = id;
                if (count + 1 < output.Length) Decode(output.Slice(count, 1), token);
            }
            throw new MangaTranslationException("本地翻译结果超过长度上限。");
        }
        finally { _native.SamplerFree(sampler); }
    }

    private void AddSampler(nint chain, nint sampler)
    {
        if (sampler == 0) throw new MangaTranslationException("无法创建本地翻译采样器。");
        _native.SamplerChainAdd(chain, sampler);
    }

    private string DecodeOutput(ReadOnlySpan<int> output)
    {
        if (output.IsEmpty) throw new MangaTranslationException("本地翻译结果为空。");
        byte[] bytes = ArrayPool<byte>.Shared.Rent(OutputBytes);
        try
        {
            int length;
            fixed (int* tokenIds = output)
            fixed (byte* data = bytes)
                length = _native.Detokenize(_vocabulary, tokenIds, output.Length, data, OutputBytes, 1, 0);
            if (length <= 0 || length > OutputBytes)
                throw new MangaTranslationException("本地翻译结果为空或超出长度上限。");
            string text = StrictUtf8.GetString(bytes, 0, length).Trim();
            return text.Length > 0 ? text : throw new MangaTranslationException("本地翻译结果为空。");
        }
        catch (DecoderFallbackException) { throw new MangaTranslationException("本地翻译结果的字符编码不完整。"); }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte CheckModelProgress(float progress, nint statePointer)
    {
        try
        {
            return GCHandle.FromIntPtr(statePointer).Target is ModelLoadState state && !state.Token.IsCancellationRequested
                ? (byte)1 : (byte)0;
        }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ObserveModelLoad(int level, nint message, nint statePointer)
    {
        if (level != 2 || message == 0) return; // GGML_LOG_LEVEL_INFO
        try
        {
            const string prefix = "offloaded ";
            string? log = Marshal.PtrToStringUTF8(message);
            if (log is null || !log.EndsWith(" layers to GPU\n", StringComparison.Ordinal)) return;
            int start = log.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) return;
            start += prefix.Length;
            int end = log.IndexOf('/', start);
            if (end > start && int.TryParse(log.AsSpan(start, end - start), out int count) &&
                GCHandle.FromIntPtr(statePointer).Target is ModelLoadState state)
                state.OffloadedLayers = count;
        }
        catch { /* 禁止异常越过原生回调边界。 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_context != 0)
            {
                _native.Synchronize(_context);
                _native.ContextFree(_context);
            }
        }
        finally
        {
            try
            {
                if (_model != 0) _native.ModelFree(_model);
            }
            finally
            {
                NativeMemory.Free(_devices);
                if (_loadStateHandle.IsAllocated) _loadStateHandle.Free();
                _modelFile?.Dispose();
            }
        }
    }

    private sealed class ModelLoadState(CancellationToken token)
    {
        internal CancellationToken Token { get; } = token;
        internal int OffloadedLayers;
    }
}
