using System.Buffers;
using System.Security.Cryptography;

namespace EhViewerForPc.Services;

internal static partial class MangaOcr
{
    // 作者 ONNX 的固定输入是按比例缩放并补白后的灰度 NHWC 张量。
    private sealed class BubblePop(MangaOcrOrt runtime, MangaOcrOrt.Session session) : IDisposable
    {
        private const string FileName = "bubblepop.onnx";
        private const string Sha256 = "6a3973677f55b4cac1618714e0565af5f41c9a2158068353abd98e115de4e319";
        private const int ModelWidth = 768;
        private const int ModelHeight = 1024;
        private const int PixelCount = ModelWidth * ModelHeight;

        internal static BubblePop? TryOpen(string modelDirectory, string runtimeDirectory)
        {
            string path = Path.Combine(modelDirectory, FileName);
            MangaOcrOrt? runtime = null;
            try
            {
                if (!MangaTranslationBackendService.IsLocalFile(path, 3_752_020)) return null;
                using FileStream file = File.OpenRead(path);
                if (!Convert.ToHexString(SHA256.HashData(file)).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
                    return null;

                runtime = new MangaOcrOrt(runtimeDirectory, useDirectMl: false);
                return new BubblePop(runtime, runtime.Open(path, ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                DllNotFoundException or MangaTranslationException)
            {
                runtime?.Dispose();
                return null;
            }
        }

        internal int?[] FindMemberships(ImagePixels image, List<MangaOcrRegion> regions, CancellationToken token)
        {
            int imageWidth = image.Width;
            int imageHeight = image.Height;
            double scale = Math.Min((double)ModelWidth / imageWidth, (double)ModelHeight / imageHeight);
            int width = Math.Max(1, (int)Math.Round(imageWidth * scale));
            int height = Math.Max(1, (int)Math.Round(imageHeight * scale));
            var placement = new Bounds((ModelWidth - width) / 2, (ModelHeight - height) / 2, width, height);
            float[] buffer = ArrayPool<float>.Shared.Rent(PixelCount);
            try
            {
                Span<float> input = buffer.AsSpan(0, PixelCount);
                input.Fill(Normalize(255));
                float xScale = (float)imageWidth / width;
                float yScale = (float)imageHeight / height;
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    int row = (y + placement.Y) * ModelWidth + placement.X;
                    for (int x = 0; x < width; x++)
                    {
                        int gray = image.Sample((x + 0.5f) * xScale - 0.5f, (y + 0.5f) * yScale - 0.5f).Gray;
                        input[row + x] = Normalize(gray);
                    }
                }

                return session.RunGrayscaleImage(input, ModelWidth, ModelHeight,
                    (logits, shape) => ReadMemberships(logits, shape, placement, regions, imageWidth, imageHeight, token));
            }
            finally { ArrayPool<float>.Shared.Return(buffer); }
        }

        private static float Normalize(int gray) => (gray / 255f - 0.449f) / 0.226f;

        private static int?[] ReadMemberships(ReadOnlySpan<float> logits, ReadOnlySpan<long> shape, Bounds placement,
            List<MangaOcrRegion> regions, int imageWidth, int imageHeight, CancellationToken token)
        {
            if (shape is not [1, ModelHeight, ModelWidth, 3] || logits.Length != PixelCount * 3)
                throw new InvalidOperationException("气泡模型输出张量尺寸不符。");

            int[] labels = ArrayPool<int>.Shared.Rent(PixelCount);
            int[] pending = ArrayPool<int>.Shared.Rent(PixelCount);
            try
            {
                Array.Clear(labels, 0, PixelCount);
                var accepted = new HashSet<int>();
                int label = 0;
                for (int y = placement.Y; y < placement.Bottom; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = placement.X; x < placement.Right; x++)
                    {
                        int cell = y * ModelWidth + x;
                        if (labels[cell] != 0 || !(logits[cell * 3] >= 0)) continue;
                        label++;
                        int count = 1;
                        double confidence = 0;
                        pending[0] = cell;
                        labels[cell] = label;
                        for (int index = 0; index < count; index++)
                        {
                            if ((index & 0x1fff) == 0) token.ThrowIfCancellationRequested();
                            int pixel = pending[index];
                            confidence += Sigmoid(logits[pixel * 3]) + Sigmoid(logits[pixel * 3 + 2]);
                            int row = pixel / ModelWidth;
                            int column = pixel % ModelWidth;
                            for (int ny = Math.Max(placement.Y, row - 1); ny <= Math.Min(placement.Bottom - 1, row + 1); ny++)
                            for (int nx = Math.Max(placement.X, column - 1); nx <= Math.Min(placement.Right - 1, column + 1); nx++)
                            {
                                int next = ny * ModelWidth + nx;
                                if (labels[next] != 0 || !(logits[next * 3] >= 0)) continue;
                                labels[next] = label;
                                pending[count++] = next;
                            }
                        }

                        // 仅用面积足够且高置信的实例阻止跨泡邻接。
                        if (count >= 60 && confidence >= 1.6 * count) accepted.Add(label);
                    }
                }

                var memberships = new int?[regions.Count];
                for (int i = 0; i < regions.Count; i++)
                    memberships[i] = MatchRegion(labels, accepted, placement, regions[i], imageWidth, imageHeight);
                return memberships;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(pending);
                ArrayPool<int>.Shared.Return(labels);
            }
        }

        private static int? MatchRegion(int[] labels, HashSet<int> accepted, Bounds placement, MangaOcrRegion region,
            int imageWidth, int imageHeight)
        {
            if (accepted.Count == 0) return null;
            int LabelAt(double x, double y)
            {
                int column = placement.X + Math.Clamp((int)(x * placement.Width / imageWidth), 0, placement.Width - 1);
                int row = placement.Y + Math.Clamp((int)(y * placement.Height / imageHeight), 0, placement.Height - 1);
                return labels[row * ModelWidth + column];
            }

            int label = 0;
            const int samples = 7;
            int support = 0;
            for (int y = 0; y < samples; y++)
            for (int x = 0; x < samples; x++)
            {
                int candidate = LabelAt(region.X + (x + 0.5) * region.Width / samples,
                    region.Y + (y + 0.5) * region.Height / samples);
                if (!accepted.Contains(candidate)) continue;
                if (label != 0 && label != candidate) return null;
                label = candidate;
                support++;
            }
            // 至少四分之三采样点属于同一高置信实例，并拒绝跨另一个已确认实例的框。
            return support >= 37 ? label : null;
        }

        private static double Sigmoid(float logit) => 1.0 / (1.0 + Math.Exp(-logit));

        public void Dispose()
        {
            try { session.Dispose(); }
            finally { runtime.Dispose(); }
        }
    }
}
