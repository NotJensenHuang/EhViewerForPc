using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ComputeSharp;

namespace EhViewerForPc.ImageSearch;

/// <summary>单设备、有界同步批次；所有调用完成 fence / 回读后才归还租约。构造不接触设备。</summary>
[SupportedOSPlatform("windows")]
public sealed partial class ComputeSharpAccelerator : IImageComputeAccelerator
{
    public const long HostWorkingSetBytes = 32L * 1024 * 1024;
    private const int MaximumPixels = 4 * 1024 * 1024;
    private const int ChunkSize = 8192;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly object DeviceGate = new();
    private static GraphicsDevice? _sharedDevice;
    private static int _deviceUsers;
    private GraphicsDevice? _device;
    private ReadWriteBuffer<uint>? _pixels, _descriptors, _centers;
    private ReadWriteBuffer<int>? _scores, _ids, _distances;
    private int _pixelCapacity, _scoreCapacity;
    private bool _attempted, _disabled, _disposed;
    private long _scoreDispatches, _assignmentDispatches;
    public string DeviceName { get; private set; } = "CPU";
    public string? FailureReason { get; private set; }
    public bool IsAvailable => !_disabled && !_disposed && _device is not null;
    public long ScoreDispatches => Interlocked.Read(ref _scoreDispatches);
    public long AssignmentDispatches => Interlocked.Read(ref _assignmentDispatches);

    public bool TryInitialize()
    {
        _gate.Wait();
        try { return EnsureDevice(); }
        catch (Exception error) { Disable(error); return false; }
        finally { _gate.Release(); }
    }

    private bool EnsureDevice()
    {
        if (_disposed || _disabled) return false;
        if (_attempted) return _device is not null;
        _attempted = true;
        lock (DeviceGate)
        {
            _sharedDevice ??= GraphicsDevice.GetDefault();
            _device = _sharedDevice;
            _deviceUsers++;
        }
        if (!_device.IsHardwareAccelerated)
        {
            Disable(new NotSupportedException("未找到支持计算的硬件 GPU。"));
            return false;
        }
        DeviceName = _device.Name;
        return true;
    }

    // ComputeSharp 的类型检查接口带 IL2026。仅开放基础 int / uint，完整保留这两种类型的元数据；
    // 不允许任意结构体进入缓冲区，NativeAOT 实际运行另有金样验证。
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(uint))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(int))]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "只实例化 int/uint buffer，两种基础类型元数据已显式保留，不接收自定义结构体。")]
    private void EnsurePixelBuffers(int pixels)
    {
        int words = (pixels + 3) / 4;
        if (_pixelCapacity < words)
        {
            _pixels?.Dispose();
            _pixels = null;
            _pixelCapacity = 0;
            int capacity = checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)words));
            _pixels = _device!.AllocateReadWriteBuffer<uint>(capacity);
            _pixelCapacity = capacity;
        }
        if (_scoreCapacity < pixels)
        {
            _scores?.Dispose();
            _scores = null;
            _scoreCapacity = 0;
            int capacity = checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)pixels));
            _scores = _device!.AllocateReadWriteBuffer<int>(capacity);
            _scoreCapacity = capacity;
        }
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(uint))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(int))]
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "仅 int/uint，元数据显式保留，固定最大容量。")]
    private void EnsureAssignmentBuffers()
    {
        _descriptors ??= _device!.AllocateReadWriteBuffer<uint>(ChunkSize * 8);
        _centers ??= _device!.AllocateReadWriteBuffer<uint>(4096 * 8);
        _ids ??= _device!.AllocateReadWriteBuffer<int>(ChunkSize);
        _distances ??= _device!.AllocateReadWriteBuffer<int>(ChunkSize);
    }

    public bool TryGetFastScores(GrayImage image, int threshold, int[] scores, CancellationToken token)
    {
        int count = image.Pixels.Length;
        if (count > MaximumPixels || scores.Length < count) return false;
        _gate.Wait(token);
        try
        {
            if (!EnsureDevice()) return false;
            EnsurePixelBuffers(count);
            int words = (count + 3) / 4;
            uint[] packed = ArrayPool<uint>.Shared.Rent(words);
            try
            {
                packed[words - 1] = 0;
                image.Pixels.AsSpan().CopyTo(MemoryMarshal.AsBytes(packed.AsSpan(0, words)));
                _pixels!.CopyFrom(packed.AsSpan(0, words), 0);
                token.ThrowIfCancellationRequested();
                using (var context = _device!.CreateComputeContext())
                {
                    for (int offset = 0; offset < count; offset += 65536)
                    {
                        token.ThrowIfCancellationRequested();
                        context.For(Math.Min(65536, count - offset), new FastScoreShader(_pixels!, _scores!, image.Width, image.Height, threshold, offset));
                    }
                }
                _scores!.CopyTo(scores.AsSpan(0, count), 0);
                Interlocked.Add(ref _scoreDispatches, (count + 65535) / 65536);
                token.ThrowIfCancellationRequested();
                return true;
            }
            finally { ArrayPool<uint>.Shared.Return(packed); }
        }
        catch (Exception error) when (error is not OperationCanceledException) { Disable(error); return false; }
        finally { _gate.Release(); }
    }

    public bool TryAssign(BinaryDescriptor[] data, BinaryDescriptor[] centers, int[] ids, out long distance, CancellationToken token)
    {
        distance = 0;
        if (centers.Length is < 1 or > 4096 || ids.Length < data.Length) return false;
        _gate.Wait(token);
        try
        {
            if (!EnsureDevice()) return false;
            EnsureAssignmentBuffers();
            uint[] words = ArrayPool<uint>.Shared.Rent(Math.Max(ChunkSize, centers.Length) * 8);
            int[]? distances = null;
            try
            {
                distances = ArrayPool<int>.Shared.Rent(ChunkSize);
                Encode(centers, words);
                _centers!.CopyFrom(words.AsSpan(0, centers.Length * 8), 0);
                int chunk = Math.Min(ChunkSize, Math.Max(256, 8 * 1024 * 1024 / centers.Length));
                for (int offset = 0; offset < data.Length; offset += chunk)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(chunk, data.Length - offset);
                    Encode(data.AsSpan(offset, count), words);
                    _descriptors!.CopyFrom(words.AsSpan(0, count * 8), 0);
                    _device!.For(count, new AssignmentShader(_descriptors!, _centers!, _ids!, _distances!, centers.Length));
                    _ids!.CopyTo(ids.AsSpan(offset, count), 0);
                    _distances!.CopyTo(distances.AsSpan(0, count), 0);
                    for (int i = 0; i < count; i++) distance += distances[i];
                    Interlocked.Increment(ref _assignmentDispatches);
                }
                token.ThrowIfCancellationRequested();
                return true;
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(words);
                if (distances is not null) ArrayPool<int>.Shared.Return(distances);
            }
        }
        catch (Exception error) when (error is not OperationCanceledException) { Disable(error); distance = 0; return false; }
        finally { _gate.Release(); }
    }

    private static void Encode(ReadOnlySpan<BinaryDescriptor> data, Span<uint> output)
    {
        for (int i = 0; i < data.Length; i++)
        {
            var value = data[i];
            output[i * 8] = (uint)value.A; output[i * 8 + 1] = (uint)(value.A >> 32);
            output[i * 8 + 2] = (uint)value.B; output[i * 8 + 3] = (uint)(value.B >> 32);
            output[i * 8 + 4] = (uint)value.C; output[i * 8 + 5] = (uint)(value.C >> 32);
            output[i * 8 + 6] = (uint)value.D; output[i * 8 + 7] = (uint)(value.D >> 32);
        }
    }

    private void Disable(Exception error)
    {
        _disabled = true;
        FailureReason = error.GetType().Name + ": " + error.Message;
        ReleaseBuffers();
    }

    private void ReleaseBuffers()
    {
        // 某一释放失败也继续处理其他资源。
        foreach (var resource in new IDisposable?[] { _pixels, _scores, _descriptors, _centers, _ids, _distances })
            try { resource?.Dispose(); } catch (Exception error) { FailureReason ??= error.GetType().Name; }
        _pixels = _descriptors = _centers = null;
        _scores = _ids = _distances = null;
        _pixelCapacity = _scoreCapacity = 0;
    }

    public void ReleaseWorkingSet()
    {
        _gate.Wait();
        try { ReleaseBuffers(); }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseBuffers();
            lock (DeviceGate)
            {
                if (_device is not null && --_deviceUsers == 0)
                {
                    try { _sharedDevice?.Dispose(); }
                    finally { _sharedDevice = null; }
                }
                _device = null;
            }
        }
        finally { _gate.Release(); }
    }

    [ThreadGroupSize(DefaultThreadGroupSizes.X)]
    [GeneratedComputeShaderDescriptor]
    internal readonly partial struct AssignmentShader(ReadWriteBuffer<uint> data, ReadWriteBuffer<uint> centers,
        ReadWriteBuffer<int> ids, ReadWriteBuffer<int> distances, int centerCount) : IComputeShader
    {
        public void Execute()
        {
            int best = 257, chosen = 0;
            for (int c = 0; c < centerCount; c++)
            {
                int distance = 0;
                for (int w = 0; w < 8; w++) distance += (int)Hlsl.CountBits(data[ThreadIds.X * 8 + w] ^ centers[c * 8 + w]);
                if (distance < best) { best = distance; chosen = c; }
            }
            ids[ThreadIds.X] = chosen; distances[ThreadIds.X] = best;
        }
    }

    [ThreadGroupSize(DefaultThreadGroupSizes.X)]
    [GeneratedComputeShaderDescriptor]
    internal readonly partial struct FastScoreShader(ReadWriteBuffer<uint> pixels, ReadWriteBuffer<int> scores,
        int width, int height, int threshold, int offset) : IComputeShader
    {
        private int Pixel(int x, int y)
        {
            int index = y * width + x;
            return (int)((pixels[index >> 2] >> ((index & 3) * 8)) & 255);
        }
        private int Difference(int x, int y, int center, int index)
        {
            int dx = 0, dy = 0;
            switch (index & 15)
            {
                case 0: dy = -3; break; case 1: dx = 1; dy = -3; break;
                case 2: dx = 2; dy = -2; break; case 3: dx = 3; dy = -1; break;
                case 4: dx = 3; break; case 5: dx = 3; dy = 1; break;
                case 6: dx = 2; dy = 2; break; case 7: dx = 1; dy = 3; break;
                case 8: dy = 3; break; case 9: dx = -1; dy = 3; break;
                case 10: dx = -2; dy = 2; break; case 11: dx = -3; dy = 1; break;
                case 12: dx = -3; break; case 13: dx = -3; dy = -1; break;
                case 14: dx = -2; dy = -2; break; default: dx = -1; dy = -3; break;
            }
            return Pixel(x + dx, y + dy) - center;
        }
        public void Execute()
        {
            int index = ThreadIds.X + offset, x = index % width, y = index / width;
            scores[index] = -1;
            if (x < 3 || y < 3 || x >= width - 3 || y >= height - 3) return;
            int center = Pixel(x, y), bright = 0, dark = 0;
            for (int i = 0; i < 16; i += 4)
            {
                int d = Difference(x, y, center, i);
                if (d > threshold) bright++;
                if (d < -threshold) dark++;
            }
            if (bright < 2 && dark < 2) return;
            int score = 0;
            for (int start = 0; start < 16; start++)
            {
                int minimum = 256, maximum = -256;
                for (int j = start; j < start + 9; j++)
                {
                    int difference = Difference(x, y, center, j);
                    minimum = Hlsl.Min(minimum, difference); maximum = Hlsl.Max(maximum, difference);
                }
                score = Hlsl.Max(score, Hlsl.Max(minimum, -maximum) - 1);
            }
            if (score >= threshold) scores[index] = score;
        }
    }
}
