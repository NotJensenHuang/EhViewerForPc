using System.IO.Compression;
using System.Runtime.InteropServices;

namespace EhViewerForPc.Services;

internal sealed unsafe class JMangaTranslator : IDisposable
{
    internal const string Provider = "OnnxRuntimeCpu";
    private const int Heads = 12, HeadSize = 64, Hidden = Heads * HeadSize;
    private const int MaxSourceTokens = 128, CrossSize = MaxSourceTokens + 2, CacheSize = 128;
    private const int Vocabulary = 22935;
    private const int CrossStride = Hidden * CrossSize, SelfStride = Hidden * CacheSize;
    private const float Masked = -10000;
    private static readonly long[] HiddenShape = [1, 1, Hidden];
    private static readonly long[] SelfShape = [1, Heads, CacheSize, HeadSize];
    private static readonly long[] CrossShape = [1, Heads, CrossSize, HeadSize];
    private static readonly long[] SelfMaskShape = [1, 1, 1, CacheSize];
    private static readonly long[] CrossMaskShape = [1, 1, 1, CrossSize];
    private static readonly string[] CrossNames = ["ck0", "cv0", "ck1", "cv1"];

    private readonly MangaOcrOrt _runtime;
    private readonly MangaOcrOrt.MultiTensorSession _encoder;
    private readonly MangaOcrOrt.MultiTensorSession _decoder;
    private readonly JMangaTokenizer _tokenizer;
    private readonly float[] _embedding;
    private readonly float[] _positions;
    // 工作进程串行处理，同一后端复用较大的 KV 缓冲区。
    private readonly float[] _cross = new float[4 * CrossStride];
    private readonly float[] _cache = new float[4 * SelfStride];
    private readonly float[] _masks = new float[CacheSize + CrossSize];
    private readonly float[] _state = new float[Hidden];
    private bool _disposed;

    private JMangaTranslator(MangaOcrOrt runtime, MangaOcrOrt.MultiTensorSession encoder,
        MangaOcrOrt.MultiTensorSession decoder, JMangaTokenizer tokenizer, float[] embedding, float[] positions)
    {
        _runtime = runtime;
        _encoder = encoder;
        _decoder = decoder;
        _tokenizer = tokenizer;
        _embedding = embedding;
        _positions = positions;
    }

    internal static JMangaTranslator Load(string runtimeDirectory, string modelDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string encoderPath = RequiredFile(modelDirectory, "onnx", "encoder_fp32.onnx");
        string decoderPath = RequiredFile(modelDirectory, "onnx", "decoder_fp32.onnx");
        string tensors = RequiredFile(modelDirectory, "onnx", "host_fp32.npz");
        var tokenizer = new JMangaTokenizer(RequiredFile(modelDirectory, "tokenizer", "tokenizer.json"),
            RequiredFile(modelDirectory, "vocab.json"), RequiredFile(modelDirectory, "joint.model"));
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(tensors);
        float[] embedding = ReadFloats(archive, "emb.npy", "(22935, 768)", Vocabulary * Hidden);
        float[] positions = ReadFloats(archive, "pos.npy", "(512, 768)", 512 * Hidden);
        float scale = MathF.Sqrt(Hidden);
        for (int i = 0; i < embedding.Length; i++) embedding[i] *= scale;
        cancellationToken.ThrowIfCancellationRequested();

        MangaOcrOrt? runtime = null;
        MangaOcrOrt.MultiTensorSession? encoder = null;
        MangaOcrOrt.MultiTensorSession? decoder = null;
        try
        {
            runtime = new MangaOcrOrt(runtimeDirectory, useDirectMl: false);
            encoder = new MangaOcrOrt.MultiTensorSession(runtime, encoderPath, ["ids"], CrossNames);
            cancellationToken.ThrowIfCancellationRequested();
            decoder = new MangaOcrOrt.MultiTensorSession(runtime, decoderPath,
                ["x", "kc0", "vc0", "kc1", "vc1", "smask", .. CrossNames, "cmask"],
                ["logits", "k0", "v0", "k1", "v1"]);
            cancellationToken.ThrowIfCancellationRequested();
            return new JMangaTranslator(runtime, encoder, decoder, tokenizer, embedding, positions);
        }
        catch
        {
            decoder?.Dispose();
            encoder?.Dispose();
            runtime?.Dispose();
            throw;
        }
    }

    internal string Translate(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        long[] ids = _tokenizer.Encode(text, MaxSourceTokens);
        int sourceSize = ids.Length + 2;
        cancellationToken.ThrowIfCancellationRequested();
        Array.Clear(_cross);
        fixed (long* source = ids)
        {
            _encoder.Run([MangaOcrOrt.TensorInput.Int64(source, ids.Length, [1, ids.Length])], (index, values) =>
            {
                if (values.Length != Hidden * sourceSize) throw new InvalidDataException("JManga 编码张量尺寸不符。");
                for (int head = 0; head < Heads; head++)
                    values.Slice(head * sourceSize * HeadSize, sourceSize * HeadSize)
                        .CopyTo(_cross.AsSpan(index * CrossStride + head * CrossSize * HeadSize));
            });
        }

        Array.Clear(_cache);
        _masks.AsSpan(0, CacheSize).Fill(Masked);
        _masks.AsSpan(CacheSize, sourceSize).Clear();
        _masks.AsSpan(CacheSize + sourceSize).Fill(Masked);
        Span<int> output = stackalloc int[CacheSize + 1];
        var inputs = new MangaOcrOrt.TensorInput[11];
        int token = 1, previous = -1, previous2 = -1;
        int step = 0;
        int limit = Math.Min(3 * ids.Length + 10, CacheSize + 1);
        void ReadStep(int index, ReadOnlySpan<float> values)
        {
            if (index == 0)
            {
                if (values.Length != Vocabulary) throw new InvalidDataException("JManga 词表尺寸不符。");
                token = _tokenizer.SelectToken(values, previous, previous2);
                return;
            }
            if (values.Length != Hidden) throw new InvalidDataException("JManga 缓存张量尺寸不符。");
            if (step == CacheSize) return;
            int start = (index - 1) * SelfStride + step * HeadSize;
            for (int head = 0; head < Heads; head++)
                values.Slice(head * HeadSize, HeadSize).CopyTo(_cache.AsSpan(start + head * CacheSize * HeadSize));
        }
        MangaOcrOrt.TensorOutputReader read = ReadStep;
        fixed (float* crossData = _cross, cacheData = _cache, maskData = _masks, stateData = _state)
        {
            inputs[0] = MangaOcrOrt.TensorInput.Float(stateData, Hidden, HiddenShape);
            for (int i = 0; i < 4; i++)
            {
                inputs[1 + i] = MangaOcrOrt.TensorInput.Float(cacheData + i * SelfStride, SelfStride, SelfShape);
                inputs[6 + i] = MangaOcrOrt.TensorInput.Float(crossData + i * CrossStride, CrossStride, CrossShape);
            }
            inputs[5] = MangaOcrOrt.TensorInput.Float(maskData, CacheSize, SelfMaskShape);
            inputs[10] = MangaOcrOrt.TensorInput.Float(maskData + CacheSize, CrossSize, CrossMaskShape);
            for (; step < limit; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int offset = token * Hidden;
                for (int i = 0; i < Hidden; i++) _state[i] = _embedding[offset + i] + _positions[step * Hidden + i];
                _decoder.Run(inputs, read);
                cancellationToken.ThrowIfCancellationRequested();
                if (token is 2 or 3) return _tokenizer.Decode(output[..step]);
                output[step] = token;
                if (step < CacheSize) _masks[step] = 0;
                previous2 = previous;
                previous = token;
            }
        }
        throw new MangaTranslationRegionLimitException(_tokenizer.Decode(output[..step]));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decoder.Dispose();
        _encoder.Dispose();
        _runtime.Dispose();
    }

    private static string RequiredFile(string directory, params string[] parts)
    {
        string path = Path.Combine(directory, Path.Combine(parts));
        if (!File.Exists(path) || !MangaTranslationBackendService.IsLocalFile(path))
            throw new MangaTranslationException("JMangaTranslator-Fast 模型文件不完整，请重新检查本地资源。", requiresSetup: true);
        return path;
    }

    private static float[] ReadFloats(ZipArchive archive, string name, string shape, int count)
    {
        ZipArchiveEntry entry = archive.GetEntry(name) ?? throw new InvalidDataException("JManga 运行权重缺失。");
        using Stream stream = entry.Open();
        Span<byte> prefix = stackalloc byte[10];
        stream.ReadExactly(prefix);
        int headerSize = prefix[8] + (prefix[9] << 8);
        if (prefix[0] != 0x93 || !prefix[1..8].SequenceEqual("NUMPY\u0001\0"u8) ||
            headerSize is 0 or > 512 || entry.Length != 10L + headerSize + count * sizeof(float))
            throw new InvalidDataException("JManga 运行权重格式不符。");
        Span<byte> header = stackalloc byte[headerSize];
        stream.ReadExactly(header);
        string description = System.Text.Encoding.ASCII.GetString(header);
        if (description.Trim() != $"{{'descr': '<f4', 'fortran_order': False, 'shape': {shape}, }}")
            throw new InvalidDataException("JManga 运行权重尺寸不符。");
        var data = new float[count];
        stream.ReadExactly(MemoryMarshal.AsBytes(data.AsSpan()));
        return data;
    }
}
