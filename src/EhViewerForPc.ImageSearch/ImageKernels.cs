using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace EhViewerForPc.ImageSearch;

/// <summary>保持标量运算顺序的像素内核；默认 AOT 使用 SSE2，JIT 可用时使用 AVX2。</summary>
public static class ImageKernels
{
    private static readonly double[] Kernel = CreateKernel();
    private static bool CanUseAvx2 => Avx2.IsSupported && Sse41.IsSupported;
    public static bool IsSimdSupported => Sse2.IsSupported;
    public static string Implementation => CanUseAvx2 ? "AVX2" : Sse2.IsSupported ? "SSE2" : "Scalar";

    public static void RgbaToGray(ReadOnlySpan<byte> rgba, Span<byte> gray, bool useSimd = true)
    {
        if (rgba.Length != checked(gray.Length * 4)) throw new ArgumentException("RGBA row size differs.");
        int x = 0;
        if (useSimd && CanUseAvx2)
        {
            var mask = Vector128.Create(255u);
            var white = Vector256.Create(255d);
            for (; x <= gray.Length - 4; x += 4)
            {
                var pixels = Vector128.LoadUnsafe(ref MemoryMarshal.GetReference(rgba), (nuint)(x * 4)).AsUInt32();
                var r = Avx.ConvertToVector256Double((pixels & mask).AsInt32());
                var g = Avx.ConvertToVector256Double((Sse2.ShiftRightLogical(pixels, 8) & mask).AsInt32());
                var b = Avx.ConvertToVector256Double((Sse2.ShiftRightLogical(pixels, 16) & mask).AsInt32());
                var alpha = Avx.ConvertToVector256Double(Sse2.ShiftRightLogical(pixels, 24).AsInt32());
                // 不使用 FMA：每一步与 .299*r + .587*g + .114*b 的原标量舍入一致。
                var value = Avx.Add(Avx.Add(Avx.Multiply(r, Vector256.Create(.299)), Avx.Multiply(g, Vector256.Create(.587))), Avx.Multiply(b, Vector256.Create(.114)));
                value = Avx.Divide(Avx.Add(Avx.Multiply(value, alpha), Avx.Multiply(white, Avx.Subtract(white, alpha))), white);
                var integral = Avx.Floor(value);
                var fraction = Avx.Subtract(value, integral);
                var increment = Avx.And(Avx.Compare(fraction, Vector256.Create(.5), FloatComparisonMode.OrderedGreaterThanOrEqualNonSignaling), Vector256.Create(1d));
                StoreBytes(gray.Slice(x, 4), Avx.ConvertToVector128Int32WithTruncation(Avx.Add(integral, increment)));
            }
        }
        else if (useSimd && Sse2.IsSupported)
        {
            var mask = Vector128.Create(255u);
            var white = Vector128.Create(255d);
            for (; x <= gray.Length - 2; x += 2)
            {
                var pixels = Vector64.LoadUnsafe(ref MemoryMarshal.GetReference(rgba), (nuint)(x * 4)).ToVector128().AsUInt32();
                var r = Sse2.ConvertToVector128Double((pixels & mask).AsInt32());
                var g = Sse2.ConvertToVector128Double((Sse2.ShiftRightLogical(pixels, 8) & mask).AsInt32());
                var b = Sse2.ConvertToVector128Double((Sse2.ShiftRightLogical(pixels, 16) & mask).AsInt32());
                var alpha = Sse2.ConvertToVector128Double(Sse2.ShiftRightLogical(pixels, 24).AsInt32());
                var value = Sse2.Add(Sse2.Add(Sse2.Multiply(r, Vector128.Create(.299)), Sse2.Multiply(g, Vector128.Create(.587))), Sse2.Multiply(b, Vector128.Create(.114)));
                value = Sse2.Divide(Sse2.Add(Sse2.Multiply(value, alpha), Sse2.Multiply(white, Sse2.Subtract(white, alpha))), white);
                gray[x] = RoundGray(value.GetElement(0));
                gray[x + 1] = RoundGray(value.GetElement(1));
            }
        }
        for (; x < gray.Length; x++)
        {
            int i = x * 4, alpha = rgba[i + 3];
            double value = .299 * rgba[i] + .587 * rgba[i + 1] + .114 * rgba[i + 2];
            gray[x] = (byte)Math.Clamp((int)Math.Round((value * alpha + 255 * (255 - alpha)) / 255, MidpointRounding.AwayFromZero), 0, 255);
        }
    }

    public static GrayImage GaussianBlur(GrayImage image, bool useSimd = true)
    {
        int width = image.Width, height = image.Height, length = image.Pixels.Length;
        // 租约仅属于本次同步调用；不进入返回的 GrayImage，也不跨 worker 共享。
        float[] horizontal = ArrayPool<float>.Shared.Rent(length);
        try
        {
            byte[] output = new byte[length];
            bool vector = useSimd && CanUseAvx2;
            bool sse = useSimd && Sse2.IsSupported;
            for (int y = 0; y < height; y++)
            {
                int start = y * width, x = 0;
                if (vector)
                {
                    for (; x < Math.Min(3, width); x++) horizontal[start + x] = Horizontal(image, x, start);
                    for (; x <= width - 7; x += 4)
                    {
                        var value = Vector256<double>.Zero;
                        for (int k = -3; k <= 3; k++)
                        {
                            uint bytes = BinaryPrimitives.ReadUInt32LittleEndian(image.Pixels.AsSpan(start + x + k, 4));
                            var expanded = Sse41.ConvertToVector128Int32(Vector128.CreateScalar(bytes).AsByte());
                            value = Avx.Add(value, Avx.Multiply(Avx.ConvertToVector256Double(expanded), Vector256.Create(Kernel[k + 3])));
                        }
                        Avx.ConvertToVector128Single(value).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(horizontal), (nuint)(start + x));
                    }
                }
                else if (sse)
                {
                    for (; x < Math.Min(3, width); x++) horizontal[start + x] = Horizontal(image, x, start);
                    for (; x <= width - 5; x += 2)
                    {
                        var value = Vector128<double>.Zero;
                        for (int k = -3; k <= 3; k++)
                        {
                            var packed = Vector128.CreateScalar((uint)BinaryPrimitives.ReadUInt16LittleEndian(image.Pixels.AsSpan(start + x + k, 2))).AsByte();
                            var shorts = Sse2.UnpackLow(packed, Vector128<byte>.Zero).AsInt16();
                            var integers = Sse2.UnpackLow(shorts, Vector128<short>.Zero).AsInt32();
                            value = Sse2.Add(value, Sse2.Multiply(Sse2.ConvertToVector128Double(integers), Vector128.Create(Kernel[k + 3])));
                        }
                        Sse2.ConvertToVector128Single(value).GetLower().StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(horizontal), (nuint)(start + x));
                    }
                }
                for (; x < width; x++) horizontal[start + x] = Horizontal(image, x, start);
            }
            Span<int> rows = stackalloc int[7];
            for (int y = 0; y < height; y++)
            {
                for (int k = -3; k <= 3; k++) rows[k + 3] = Reflect(y + k, height) * width;
                int x = 0;
                if (vector)
                {
                    for (; x <= width - 4; x += 4)
                    {
                        var value = Vector256<double>.Zero;
                        for (int k = 0; k < 7; k++)
                        {
                            var pixels = Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(horizontal), (nuint)(rows[k] + x));
                            value = Avx.Add(value, Avx.Multiply(Avx.ConvertToVector256Double(pixels), Vector256.Create(Kernel[k])));
                        }
                        StoreBytes(output.AsSpan(y * width + x, 4), Avx.ConvertToVector128Int32WithTruncation(Avx.RoundToNearestInteger(value)));
                    }
                }
                else if (sse)
                {
                    for (; x <= width - 2; x += 2)
                    {
                        var value = Vector128<double>.Zero;
                        for (int k = 0; k < 7; k++)
                        {
                            var pixels = Vector64.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(horizontal), (nuint)(rows[k] + x)).ToVector128();
                            value = Sse2.Add(value, Sse2.Multiply(Sse2.ConvertToVector128Double(pixels), Vector128.Create(Kernel[k])));
                        }
                        output[y * width + x] = (byte)Math.Clamp((int)Math.Round(value.GetElement(0)), 0, 255);
                        output[y * width + x + 1] = (byte)Math.Clamp((int)Math.Round(value.GetElement(1)), 0, 255);
                    }
                }
                for (; x < width; x++)
                {
                    double value = 0;
                    for (int k = 0; k < 7; k++) value += Kernel[k] * horizontal[rows[k] + x];
                    output[y * width + x] = (byte)Math.Clamp((int)Math.Round(value), 0, 255);
                }
            }
            return new(width, height, output);
        }
        finally { ArrayPool<float>.Shared.Return(horizontal); }
    }

    private static byte RoundGray(double value) => (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);

    private static float Horizontal(GrayImage image, int x, int start)
    {
        double value = 0;
        for (int k = -3; k <= 3; k++) value += Kernel[k + 3] * image.Pixels[start + Reflect(x + k, image.Width)];
        return (float)value;
    }

    private static int Reflect(int coordinate, int size)
    {
        if (size == 1) return 0;
        while ((uint)coordinate >= (uint)size) coordinate = coordinate < 0 ? -coordinate : 2 * size - coordinate - 2;
        return coordinate;
    }

    private static void StoreBytes(Span<byte> destination, Vector128<int> value)
    {
        var shorts = Sse41.PackUnsignedSaturate(value, Vector128<int>.Zero);
        var bytes = Sse2.PackUnsignedSaturate(shorts.AsInt16(), Vector128<short>.Zero);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, bytes.AsUInt32().GetElement(0));
    }

    private static double[] CreateKernel()
    {
        double[] kernel = Enumerable.Range(-3, 7).Select(x => Math.Exp(-x * x / 8.0)).ToArray();
        double sum = kernel.Sum();
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        return kernel;
    }
}
