using System.Runtime.InteropServices;
using System.Text.Json;

namespace EhViewerForPc.Services;

internal delegate T MangaOcrTensorReader<T>(ReadOnlySpan<float> values, ReadOnlySpan<long> shape);

/// <summary>Windows x64 的 ONNX Runtime C API。工作进程内的每个模型只顺序执行。</summary>
internal sealed unsafe partial class MangaOcrOrt : IDisposable
{
    public const string Provider = "DmlExecutionProvider";
    private const uint ApiVersion = 23;
    private const string NativeVersion = "1.23.0";
    private const uint LoadDllDirectoryAndSystem32 = 0x00000100 | 0x00000800;
    private const int FloatTensor = 1;
    private readonly bool _useDirectMl;
    private nint _directMl;
    private nint _library;
    private nint* _api;
    private nint _environment;
    private nint _allocator;
    private nint _memoryInfo;

    // 官方 Microsoft.ML.OnnxRuntime.DirectML 1.23.0 NuGet 包的 onnxruntime_c_api.h 中，
    // OrtApi 从首字段起按指针编号。保持随包 DLL 与下面的 API 版本一致。
    private enum Slot
    {
        GetErrorMessage = 2,
        CreateEnv = 3,
        DisableTelemetryEvents = 6,
        CreateSession = 7,
        Run = 9,
        CreateSessionOptions = 10,
        SetSessionExecutionMode = 13,
        EnableProfiling = 14,
        DisableMemPattern = 17,
        SetIntraOpNumThreads = 24,
        SetInterOpNumThreads = 25,
        SessionGetInputCount = 30,
        SessionGetOutputCount = 31,
        SessionGetInputName = 36,
        SessionGetOutputName = 37,
        CreateTensorWithDataAsOrtValue = 49,
        GetTensorMutableData = 51,
        GetTensorElementType = 60,
        GetDimensionsCount = 61,
        GetDimensions = 62,
        GetTensorShapeElementCount = 64,
        GetTensorTypeAndShape = 65,
        CreateCpuMemoryInfo = 69,
        AllocatorFree = 76,
        GetAllocatorWithDefaultOptions = 78,
        ReleaseEnv = 92,
        ReleaseStatus = 93,
        ReleaseMemoryInfo = 94,
        ReleaseSession = 95,
        ReleaseValue = 96,
        ReleaseTensorTypeAndShapeInfo = 99,
        ReleaseSessionOptions = 100,
        SessionEndProfiling = 110,
    }

    internal MangaOcrOrt(string runtimeDirectory, bool useDirectMl = true)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException();
        _useDirectMl = useDirectMl;
        string directory = Path.Combine(runtimeDirectory, "ort");
        try
        {
            if (useDirectMl) _directMl = LoadPackagedLibrary(Path.Combine(directory, "DirectML.dll"));
            _library = LoadPackagedLibrary(Path.Combine(directory, "onnxruntime.dll"));
            var getApiBase = (delegate* unmanaged<nint*>)NativeLibrary.GetExport(_library, "OrtGetApiBase");
            nint* apiBase = getApiBase();
            if (apiBase == null || Marshal.PtrToStringUTF8(((delegate* unmanaged<nint>)apiBase[1])()) != NativeVersion)
                throw new MangaTranslationException("应用随附的 ONNX Runtime 版本不匹配，请重新解压应用。", requiresSetup: true);
            _api = ((delegate* unmanaged<uint, nint*>)apiBase[0])(ApiVersion);
            if (_api == null) throw new InvalidOperationException("无法获取 ONNX Runtime C API。");

            nint environment = 0;
            fixed (byte* logId = "EhViewerForPc\0"u8)
                Check(((delegate* unmanaged<int, byte*, nint*, nint>)At(Slot.CreateEnv))(3, logId, &environment));
            _environment = environment;
            Check(((delegate* unmanaged<nint, nint>)At(Slot.DisableTelemetryEvents))(_environment));
            nint allocator = 0;
            Check(((delegate* unmanaged<nint*, nint>)At(Slot.GetAllocatorWithDefaultOptions))(&allocator));
            _allocator = allocator;
            nint memoryInfo = 0;
            // OrtArenaAllocator, OrtMemTypeDefault。ORT 借用固定的托管输入缓冲区。
            Check(((delegate* unmanaged<int, int, nint*, nint>)At(Slot.CreateCpuMemoryInfo))(1, 0, &memoryInfo));
            _memoryInfo = memoryInfo;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal Session Open(string modelPath, string profilePrefix)
    {
        if (!File.Exists(modelPath) || !MangaTranslationBackendService.IsLocalFile(modelPath))
            throw new MangaTranslationException("文字识别模型缺失或路径不可用。", requiresSetup: true);
        return new Session(this, modelPath, profilePrefix);
    }

    private nint At(Slot slot) => _api[(int)slot];

    private void Check(nint status)
    {
        if (status == 0) return;
        try
        {
            nint message = ((delegate* unmanaged<nint, nint>)At(Slot.GetErrorMessage))(status);
            throw new InvalidOperationException(Marshal.PtrToStringUTF8(message) ?? "ONNX Runtime 调用失败。");
        }
        finally { Release(Slot.ReleaseStatus, status); }
    }

    private void Release(Slot slot, nint pointer)
    {
        if (pointer != 0) ((delegate* unmanaged<nint, void>)At(slot))(pointer);
    }

    public void Dispose()
    {
        Release(Slot.ReleaseMemoryInfo, _memoryInfo);
        _memoryInfo = 0;
        Release(Slot.ReleaseEnv, _environment);
        _environment = 0;
        NativeLibrary.Free(_library);
        _library = 0;
        NativeLibrary.Free(_directMl);
        _directMl = 0;
    }

    private static nint LoadPackagedLibrary(string path)
    {
        if (!File.Exists(path))
            throw new MangaTranslationException("应用随附的文字识别运行时不完整，请重新解压应用。", requiresSetup: true);
        nint handle = LoadLibrary(path, 0, LoadDllDirectoryAndSystem32);
        if (handle == 0) throw new DllNotFoundException($"文字识别运行库加载失败（{Marshal.GetLastPInvokeError()}）。");
        return handle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LoadLibrary(string fileName, nint file, uint flags);

    internal sealed class Session : IDisposable
    {
        private readonly MangaOcrOrt _runtime;
        private nint _session;
        private nint _inputName;
        private nint _outputName;
        private bool _gpuVerified;

        internal Session(MangaOcrOrt runtime, string modelPath, string profilePrefix)
        {
            _runtime = runtime;
            nint options = 0;
            try
            {
                runtime.Check(((delegate* unmanaged<nint*, nint>)runtime.At(Slot.CreateSessionOptions))(&options));
                SetOption(Slot.SetSessionExecutionMode, options, 0); // ORT_SEQUENTIAL
                SetOption(Slot.SetIntraOpNumThreads, options, 1);
                SetOption(Slot.SetInterOpNumThreads, options, 1);
                if (runtime._useDirectMl)
                {
                    runtime.Check(((delegate* unmanaged<nint, nint>)runtime.At(Slot.DisableMemPattern))(options));
                    var appendDirectMl = (delegate* unmanaged<nint, int, nint>)NativeLibrary.GetExport(
                        runtime._library, "OrtSessionOptionsAppendExecutionProvider_DML");
                    runtime.Check(appendDirectMl(options, 0));
                    fixed (char* prefix = profilePrefix)
                        runtime.Check(((delegate* unmanaged<nint, char*, nint>)runtime.At(Slot.EnableProfiling))(options, prefix));
                }

                nint session = 0;
                fixed (char* model = modelPath)
                    runtime.Check(((delegate* unmanaged<nint, char*, nint, nint*, nint>)runtime.At(Slot.CreateSession))(
                        runtime._environment, model, options, &session));
                _session = session;
                _inputName = GetName(Slot.SessionGetInputCount, Slot.SessionGetInputName);
                _outputName = GetName(Slot.SessionGetOutputCount, Slot.SessionGetOutputName);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally { runtime.Release(Slot.ReleaseSessionOptions, options); }
        }

        internal bool IsGpuVerified => _gpuVerified;

        internal T Run<T>(float[] input, int width, int height, MangaOcrTensorReader<T> readResult)
        {
            if (width <= 0 || height <= 0 || width * (long)height * 3 != input.Length)
                throw new ArgumentException("文字识别输入尺寸无效。", nameof(input));
            nint inputValue = 0;
            nint outputValue = 0;
            fixed (float* pixels = input)
            {
                try
                {
                    long* shape = stackalloc long[4] { 1, 3, height, width };
                    _runtime.Check(((delegate* unmanaged<nint, float*, nuint, long*, nuint, int, nint*, nint>)
                        _runtime.At(Slot.CreateTensorWithDataAsOrtValue))(_runtime._memoryInfo, pixels,
                            (nuint)input.Length * sizeof(float), shape, 4, FloatTensor, &inputValue));
                    nint inputName = _inputName;
                    nint outputName = _outputName;
                    _runtime.Check(((delegate* unmanaged<nint, nint, nint*, nint*, nuint, nint*, nuint, nint*, nint>)
                        _runtime.At(Slot.Run))(_session, 0, &inputName, &inputValue, 1, &outputName, 1, &outputValue));
                    if (_runtime._useDirectMl && !_gpuVerified) VerifyGpuRun();
                    return ReadOutput(outputValue, readResult);
                }
                finally
                {
                    _runtime.Release(Slot.ReleaseValue, outputValue);
                    _runtime.Release(Slot.ReleaseValue, inputValue);
                }
            }
        }

        private T ReadOutput<T>(nint value, MangaOcrTensorReader<T> read)
        {
            nint info = 0;
            _runtime.Check(((delegate* unmanaged<nint, nint*, nint>)_runtime.At(Slot.GetTensorTypeAndShape))(value, &info));
            try
            {
                int elementType;
                nuint rank;
                nuint size;
                _runtime.Check(((delegate* unmanaged<nint, int*, nint>)_runtime.At(Slot.GetTensorElementType))(info, &elementType));
                _runtime.Check(((delegate* unmanaged<nint, nuint*, nint>)_runtime.At(Slot.GetDimensionsCount))(info, &rank));
                _runtime.Check(((delegate* unmanaged<nint, nuint*, nint>)_runtime.At(Slot.GetTensorShapeElementCount))(info, &size));
                if (elementType != FloatTensor || rank is 0 or > 8 || size > int.MaxValue)
                    throw new InvalidOperationException("文字识别模型的输出张量格式不匹配。");

                long* dimensions = stackalloc long[(int)rank];
                _runtime.Check(((delegate* unmanaged<nint, long*, nuint, nint>)_runtime.At(Slot.GetDimensions))(info, dimensions, rank));
                float* data;
                _runtime.Check(((delegate* unmanaged<nint, float**, nint>)_runtime.At(Slot.GetTensorMutableData))(value, &data));
                return read(new ReadOnlySpan<float>(data, (int)size), new ReadOnlySpan<long>(dimensions, (int)rank));
            }
            finally { _runtime.Release(Slot.ReleaseTensorTypeAndShapeInfo, info); }
        }

        private void VerifyGpuRun()
        {
            nint value = 0;
            _runtime.Check(((delegate* unmanaged<nint, nint, nint*, nint>)_runtime.At(Slot.SessionEndProfiling))(
                _session, _runtime._allocator, &value));
            string? path;
            try { path = Marshal.PtrToStringUTF8(value); }
            finally { FreeAllocated(value); }
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("无法确认文字识别 GPU 执行情况。");

            // DirectML 可以让少数算子留在 CPU。只有实际执行过 DirectML 节点才接受本次结果。
            using (FileStream trace = File.OpenRead(path))
            using (JsonDocument profile = JsonDocument.Parse(trace))
            {
                foreach (JsonElement entry in profile.RootElement.EnumerateArray())
                {
                    if (entry.TryGetProperty("args", out JsonElement args) && args.TryGetProperty("provider", out JsonElement provider) &&
                        provider.ValueEquals(Provider))
                    {
                        _gpuVerified = true;
                        break;
                    }
                }
            }
            File.Delete(path);
            if (!_gpuVerified) throw new InvalidOperationException("文字识别模型未在 DirectML 上执行。");
        }

        private nint GetName(Slot countSlot, Slot nameSlot)
        {
            nuint count;
            _runtime.Check(((delegate* unmanaged<nint, nuint*, nint>)_runtime.At(countSlot))(_session, &count));
            if (count != 1) throw new InvalidOperationException("文字识别模型需要一个输入及一个输出。");
            nint name = 0;
            _runtime.Check(((delegate* unmanaged<nint, nuint, nint, nint*, nint>)_runtime.At(nameSlot))(
                _session, 0, _runtime._allocator, &name));
            return name;
        }

        private void SetOption(Slot slot, nint options, int value) =>
            _runtime.Check(((delegate* unmanaged<nint, int, nint>)_runtime.At(slot))(options, value));

        private void FreeAllocated(nint pointer)
        {
            if (pointer == 0) return;
            nint status = ((delegate* unmanaged<nint, nint, nint>)_runtime.At(Slot.AllocatorFree))(_runtime._allocator, pointer);
            _runtime.Release(Slot.ReleaseStatus, status);
        }

        public void Dispose()
        {
            FreeAllocated(_outputName);
            _outputName = 0;
            FreeAllocated(_inputName);
            _inputName = 0;
            _runtime.Release(Slot.ReleaseSession, _session);
            _session = 0;
        }
    }
}
