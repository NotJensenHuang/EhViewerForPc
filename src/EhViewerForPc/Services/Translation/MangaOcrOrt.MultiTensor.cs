using System.Runtime.InteropServices;

namespace EhViewerForPc.Services;

internal sealed unsafe partial class MangaOcrOrt
{
    internal readonly record struct TensorInput(nint Data, int ByteCount, int ElementType, long[] Shape)
    {
        public static TensorInput Float(float* data, int count, long[] shape) =>
            new((nint)data, checked(count * sizeof(float)), FloatTensor, shape);

        public static TensorInput Int64(long* data, int count, long[] shape) =>
            new((nint)data, checked(count * sizeof(long)), 7, shape); // ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64
    }

    internal delegate void TensorOutputReader(int index, ReadOnlySpan<float> values);

    internal sealed class MultiTensorSession : IDisposable
    {
        private readonly MangaOcrOrt _runtime;
        private readonly nint[] _inputNames;
        private readonly nint[] _outputNames;
        private nint _session;

        internal MultiTensorSession(MangaOcrOrt runtime, string path, string[] inputNames, string[] outputNames)
        {
            _runtime = runtime;
            _inputNames = new nint[inputNames.Length];
            _outputNames = new nint[outputNames.Length];
            nint options = 0;
            try
            {
                runtime.Check(((delegate* unmanaged<nint*, nint>)runtime.At(Slot.CreateSessionOptions))(&options));
                runtime.Check(((delegate* unmanaged<nint, int, nint>)runtime.At(Slot.SetIntraOpNumThreads))(
                    options, Math.Clamp(Environment.ProcessorCount, 1, 6)));
                runtime.Check(((delegate* unmanaged<nint, int, nint>)runtime.At(Slot.SetInterOpNumThreads))(options, 1));
                runtime.Check(((delegate* unmanaged<nint, nint>)runtime.At(Slot.DisableMemPattern))(options));
                nint session = 0;
                fixed (char* file = path)
                {
                    runtime.Check(((delegate* unmanaged<nint, char*, nint, nint*, nint>)runtime.At(Slot.CreateSession))(
                        runtime._environment, file, options, &session));
                }
                _session = session;
                LoadNames(inputNames, _inputNames, Slot.SessionGetInputCount, Slot.SessionGetInputName);
                LoadNames(outputNames, _outputNames, Slot.SessionGetOutputCount, Slot.SessionGetOutputName);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally { runtime.Release(Slot.ReleaseSessionOptions, options); }
        }

        private void LoadNames(string[] expected, nint[] names, Slot countSlot, Slot nameSlot)
        {
            nuint count;
            _runtime.Check(((delegate* unmanaged<nint, nuint*, nint>)_runtime.At(countSlot))(_session, &count));
            if (count != (nuint)names.Length) throw new InvalidDataException("翻译模型的张量数量不匹配。");
            for (int i = 0; i < names.Length; i++)
            {
                nint name = 0;
                _runtime.Check(((delegate* unmanaged<nint, nuint, nint, nint*, nint>)_runtime.At(nameSlot))(
                    _session, (nuint)i, _runtime._allocator, &name));
                names[i] = name;
                if (Marshal.PtrToStringUTF8(name) != expected[i])
                    throw new InvalidDataException("翻译模型的张量签名不匹配。");
            }
        }

        internal void Run(ReadOnlySpan<TensorInput> tensors, TensorOutputReader read)
        {
            if (tensors.Length != _inputNames.Length) throw new ArgumentException(nameof(tensors));
            Span<nint> inputs = stackalloc nint[_inputNames.Length];
            Span<nint> outputs = stackalloc nint[_outputNames.Length];
            inputs.Clear();
            outputs.Clear();
            try
            {
                for (int i = 0; i < tensors.Length; i++)
                {
                    TensorInput tensor = tensors[i];
                    nint value = 0;
                    fixed (long* shape = tensor.Shape)
                    {
                        _runtime.Check(((delegate* unmanaged<nint, nint, nuint, long*, nuint, int, nint*, nint>)
                            _runtime.At(Slot.CreateTensorWithDataAsOrtValue))(_runtime._memoryInfo, tensor.Data,
                                (nuint)tensor.ByteCount, shape, (nuint)tensor.Shape.Length, tensor.ElementType, &value));
                    }
                    inputs[i] = value;
                }
                fixed (nint* inputNames = _inputNames)
                fixed (nint* outputNames = _outputNames)
                fixed (nint* inputValues = inputs)
                fixed (nint* outputValues = outputs)
                {
                    _runtime.Check(((delegate* unmanaged<nint, nint, nint*, nint*, nuint, nint*, nuint, nint*, nint>)
                        _runtime.At(Slot.Run))(_session, 0, inputNames, inputValues, (nuint)inputs.Length,
                            outputNames, (nuint)outputs.Length, outputValues));
                }
                for (int i = 0; i < outputs.Length; i++)
                    ReadOutput(outputs[i], i, read);
            }
            finally
            {
                foreach (nint value in outputs) _runtime.Release(Slot.ReleaseValue, value);
                foreach (nint value in inputs) _runtime.Release(Slot.ReleaseValue, value);
            }
        }

        private void ReadOutput(nint value, int index, TensorOutputReader read)
        {
            nint info = 0;
            _runtime.Check(((delegate* unmanaged<nint, nint*, nint>)_runtime.At(Slot.GetTensorTypeAndShape))(value, &info));
            try
            {
                int type;
                nuint elements;
                _runtime.Check(((delegate* unmanaged<nint, int*, nint>)_runtime.At(Slot.GetTensorElementType))(info, &type));
                _runtime.Check(((delegate* unmanaged<nint, nuint*, nint>)_runtime.At(Slot.GetTensorShapeElementCount))(info, &elements));
                if (type != FloatTensor || elements > int.MaxValue)
                    throw new InvalidDataException("翻译模型的输出张量格式不匹配。");
                float* data;
                _runtime.Check(((delegate* unmanaged<nint, float**, nint>)_runtime.At(Slot.GetTensorMutableData))(value, &data));
                read(index, new ReadOnlySpan<float>(data, (int)elements));
            }
            finally { _runtime.Release(Slot.ReleaseTensorTypeAndShapeInfo, info); }
        }

        public void Dispose()
        {
            foreach (nint[] names in new[] { _inputNames, _outputNames })
            {
                for (int i = 0; i < names.Length; i++)
                {
                    if (names[i] == 0) continue;
                    nint status = ((delegate* unmanaged<nint, nint, nint>)_runtime.At(Slot.AllocatorFree))(
                        _runtime._allocator, names[i]);
                    _runtime.Release(Slot.ReleaseStatus, status);
                    names[i] = 0;
                }
            }
            _runtime.Release(Slot.ReleaseSession, _session);
            _session = 0;
        }
    }
}
