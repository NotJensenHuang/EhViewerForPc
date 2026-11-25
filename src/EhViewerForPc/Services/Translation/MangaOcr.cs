using System.Text;
using SkiaSharp;

namespace EhViewerForPc.Services;

internal static partial class MangaOcr
{
    private const int MaxDetectionDimension = 1536;
    private const int RecognitionHeight = 48;
    private const float MinimumRecognitionConfidence = 0.50f;
    private const int MaxPixels = 40_000_000;
    private const int MaxInputBytes = 160 * 1024 * 1024;
    private const string SmallKana = "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮ";

    private readonly record struct Recognition(string Text, float Confidence)
    {
        internal float Score => Confidence + (ContainsKana(Text) ? 0.06f : 0);
    }

    internal static MangaOcrResult Read(string imagePath, string modelDirectory, string runtimeDirectory, CancellationToken token)
    {
        using var engine = new Engine(modelDirectory, runtimeDirectory);
        return engine.Read(imagePath, token);
    }

    internal sealed class Engine(string modelDirectory, string runtimeDirectory) : IDisposable
    {
        private readonly MangaOcrOrt _ort = new(runtimeDirectory);
        private MangaOcrOrt.Session? _detector;
        private RecognitionSessions? _recognizer;
        private string[]? _dictionary;

        internal MangaOcrResult Read(string imagePath, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using SKBitmap bitmap = DecodeImage(imagePath);
            int width = bitmap.Width;
            int height = bitmap.Height;
            var pixels = new ImagePixels(bitmap.GetPixelSpan(), width, height, bitmap.RowBytes, 4);
            string workingDirectory = Path.GetDirectoryName(imagePath)!;
            MangaOcrOrt.Session detector = _detector ??= _ort.Open(
                Path.Combine(modelDirectory, "det.onnx"), Path.Combine(workingDirectory, "det-profile"));
            double scale = Math.Min(1.0, (double)MaxDetectionDimension / Math.Max(width, height));
            int inputWidth = Math.Max(32, (int)Math.Round(width * scale / 32) * 32);
            int inputHeight = Math.Max(32, (int)Math.Round(height * scale / 32) * 32);
            float[] data = PrepareDetection(pixels, inputWidth, inputHeight, token);
            List<Quad> boxes = detector.Run(data, inputWidth, inputHeight,
                (probability, shape) => DetectBoxes(probability, shape, width, height, token));
            token.ThrowIfCancellationRequested();
            if (boxes.Count == 0) return new MangaOcrResult(width, height, [], MangaOcrOrt.Provider);

            string[] dictionary = _dictionary ??= LoadDictionary(Path.Combine(modelDirectory, "rec-dict.txt"));
            RecognitionSessions recognizer = _recognizer ??= new(_ort, Path.Combine(modelDirectory, "rec.onnx"));
            Func<Crop, Recognition> recognize = crop => RecognizeCrop(crop, recognizer, workingDirectory, dictionary, token);
            var regions = new List<MangaOcrRegion>(boxes.Count);
            foreach (Quad box in boxes)
            {
                token.ThrowIfCancellationRequested();
                Crop crop = CropRegion(pixels, box, token);
                Recognition reading = RecognizeLine(crop, recognize);
                if (reading.Confidence < MinimumRecognitionConfidence)
                    reading = RecoverVerticalColumns(crop, recognize, token);
                if (reading.Confidence < MinimumRecognitionConfidence || reading.Text.Length == 0) continue;
                Bounds bounds = box.GetBounds();
                int left = Math.Max(0, bounds.X);
                int top = Math.Max(0, bounds.Y);
                int right = Math.Min(width, bounds.Right);
                int bottom = Math.Min(height, bounds.Bottom);
                if (right > left && bottom > top)
                    regions.Add(new MangaOcrRegion(left, top, right - left, bottom - top, reading.Text));
            }
            token.ThrowIfCancellationRequested();
            var dialogue = ArrangeDialogue(regions, bitmap.GetPixelSpan(), width, height, bitmap.RowBytes, token);
            recognizer.MarkPageComplete();
            return new MangaOcrResult(width, height, dialogue, MangaOcrOrt.Provider);
        }

        internal void DiscardUnverifiedSessions()
        {
            // 未结束首次 DirectML 核验的会话仍引用当前页的临时记录路径。
            try { _recognizer?.DiscardUnverifiedSessions(); }
            finally
            {
                if (_detector is { IsGpuVerified: false })
                {
                    MangaOcrOrt.Session detector = _detector;
                    _detector = null;
                    detector.Dispose();
                }
            }
        }

        public void Dispose()
        {
            try { _recognizer?.Dispose(); }
            finally
            {
                try { _detector?.Dispose(); }
                finally { _ort.Dispose(); }
            }
        }
    }

    private sealed class RecognitionSessions(MangaOcrOrt runtime, string modelPath) : IDisposable
    {
        private const int MaxFixedSessions = 8;
        private readonly Dictionary<int, MangaOcrOrt.Session> _fixed = new(MaxFixedSessions);
        private readonly HashSet<int> _seenWidths = [];
        private MangaOcrOrt.Session? _dynamic;
        private bool _fixedDisabled;
        private bool _hasReadPage;

        internal void MarkPageComplete() => _hasReadPage = true;

        internal Recognition Run(float[] data, int width, string directory, MangaOcrTensorReader<Recognition> read,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!_fixedDisabled)
            {
                try
                {
                    // 首次识别沿用动态路径。从下一页开始，仅为重复宽度编译专用会话。
                    if (!_fixed.TryGetValue(width, out var session) && _fixed.Count < MaxFixedSessions &&
                        !_seenWidths.Add(width) && _hasReadPage)
                    {
                        session = runtime.Open(modelPath, Path.Combine(directory, $"rec-profile-{width}"), width);
                        _fixed.Add(width, session);
                    }
                    if (session is not null)
                    {
                        token.ThrowIfCancellationRequested();
                        return session.Run(data, width, RecognitionHeight, read);
                    }
                }
                catch (InvalidOperationException)
                {
                    // 设备可能无法编译固定形状或分配额外显存，释放专用会话后沿用动态 GPU 路径。
                    _fixedDisabled = true;
                    DisposeFixed();
                }
            }

            token.ThrowIfCancellationRequested();
            _dynamic ??= runtime.Open(modelPath, Path.Combine(directory, "rec-profile"));
            return _dynamic.Run(data, width, RecognitionHeight, read);
        }

        internal void DiscardUnverifiedSessions()
        {
            foreach ((int width, MangaOcrOrt.Session session) in _fixed)
            {
                if (session.IsGpuVerified) continue;
                _fixed.Remove(width);
                session.Dispose();
            }
            if (_dynamic is { IsGpuVerified: false } dynamic)
            {
                _dynamic = null;
                dynamic.Dispose();
            }
        }

        private void DisposeFixed()
        {
            foreach (MangaOcrOrt.Session session in _fixed.Values) session.Dispose();
            _fixed.Clear();
        }

        public void Dispose()
        {
            try { DisposeFixed(); }
            finally { _dynamic?.Dispose(); }
        }
    }

    private static SKBitmap DecodeImage(string path)
    {
        try
        {
            using FileStream file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxInputBytes) throw new InvalidDataException("输入图像文件过大。");
            using SKCodec? codec = SKCodec.Create(file);
            if (codec is null || codec.EncodedFormat != SKEncodedImageFormat.Png)
                throw new InvalidDataException("无法读取本地翻译的输入快照。");
            int width = codec.Info.Width;
            int height = codec.Info.Height;
            if (width < 1 || height < 1 || (long)width * height > MaxPixels)
                throw new InvalidDataException("输入图像像素超出本地翻译处理范围。");

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            try
            {
                if (bitmap.GetPixels() == 0 || codec.GetPixels(info, bitmap.GetPixels(), SKCodecOptions.Default) != SKCodecResult.Success)
                    throw new InvalidDataException("无法解码本地翻译的输入快照。");
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("无法读取本地翻译的输入快照。", ex);
        }
    }

    private static string[] LoadDictionary(string path)
    {
        if (!MangaTranslationBackendService.IsLocalFile(path, 74012))
            throw new MangaTranslationException("文字识别字典缺失或不完整。", requiresSetup: true);
        string[] lines = File.ReadAllLines(path);
        string[] dictionary = new string[lines.Length + 2];
        lines.CopyTo(dictionary, 1);
        dictionary[0] = "";
        dictionary[^1] = " ";
        return dictionary;
    }

    private static Recognition RecognizeLine(Crop crop, Func<Crop, Recognition> recognize, bool cleaned = false)
    {
        if (crop.Height <= crop.Width * 1.5) return recognize(crop);
        Recognition best = recognize(RotateVertical(crop, clockwise: false));
        bool strong = best.Confidence >= 0.85f && ContainsKana(best.Text);
        if (strong && !cleaned)
        {
            // 平均置信度只覆盖已解码字符。字形分段支持当前长度时，再检查有无漏字。
            if (CleanBrightVerticalGlyphs(crop, Math.Max(2, best.Text.Length)) is { } upright)
            {
                Recognition reading = recognize(upright);
                if (RestoresOmittedGlyphs(best, reading))
                    return reading;
            }
            return best;
        }
        if (!strong)
        {
            Recognition reversed = recognize(RotateVertical(crop, clockwise: true));
            if (reversed.Score > best.Score) best = reversed;
        }
        if (LayOutVerticalGlyphs(crop) is { } horizontal)
        {
            Recognition reading = recognize(horizontal);
            if (cleaned ? ContainsKana(reading.Text) && PreferCleanedVertical(best, reading) : reading.Score > best.Score)
                best = reading;
        }
        if (!cleaned && CleanBrightVerticalGlyphs(crop) is { } cleanedGlyphs)
        {
            Recognition reading = recognize(cleanedGlyphs);
            if (PreferCleanedVertical(best, reading) || RestoresOmittedGlyphs(best, reading)) best = reading;
        }
        return best;
    }

    private static Recognition RecoverVerticalColumns(Crop crop, Func<Crop, Recognition> recognize, CancellationToken token)
    {
        if (SplitVerticalColumns(crop, token) is not { } columns) return new Recognition("", 0);
        // 一次检测把相邻竖列框到一起时，逐列识别，仍以原检测框提交一次翻译。
        Recognition right = RecognizeLine(columns.Right, recognize, cleaned: true);
        if (right.Confidence < 0.65f || !ContainsKana(right.Text)) return new Recognition("", 0);
        Recognition left = RecognizeLine(columns.Left, recognize, cleaned: true);
        if (left.Confidence < 0.65f || !ContainsKana(left.Text)) return new Recognition("", 0);
        return new Recognition(string.Concat(right.Text, left.Text), Math.Min(right.Confidence, left.Confidence));
    }

    private static Recognition RecognizeCrop(Crop crop, RecognitionSessions recognizer, string directory, string[] dictionary,
        CancellationToken token)
    {
        int contentWidth = Math.Clamp((int)Math.Round((double)RecognitionHeight * crop.Width / crop.Height), 1, MaxDetectionDimension);
        int inputWidth = Math.Max(64, (contentWidth + 31) / 32 * 32);
        float[] data = PrepareRecognition(crop, contentWidth, inputWidth, token);
        return recognizer.Run(data, inputWidth, directory,
            (probability, shape) => DecodeCtc(probability, shape, dictionary, token), token);
    }

    private static Recognition DecodeCtc(ReadOnlySpan<float> probability, ReadOnlySpan<long> shape, string[] dictionary, CancellationToken token)
    {
        if (shape.Length != 3 || shape[0] != 1 || shape[1] < 1 || shape[1] > 2048 || shape[2] != dictionary.Length ||
            shape[1] * shape[2] != probability.Length)
            throw new MangaTranslationException("文字识别模型与字典不匹配。", requiresSetup: true);

        var text = new StringBuilder();
        int symbols = 0;
        int previous = 0;
        double confidence = 0;
        for (int position = 0; position < shape[1]; position++)
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<float> scores = probability.Slice(position * dictionary.Length, dictionary.Length);
            int best = 0;
            float score = scores[0];
            for (int i = 1; i < scores.Length; i++)
            {
                if (scores[i] <= score || !float.IsFinite(scores[i])) continue;
                best = i;
                score = scores[i];
            }
            if (best != 0 && best != previous)
            {
                text.Append(dictionary[best]);
                confidence += score;
                symbols++;
            }
            previous = best;
        }

        return new Recognition(text.ToString().Trim(), symbols == 0 ? 0 : (float)(confidence / symbols));
    }

    private static bool ContainsKana(string text)
    {
        foreach (char c in text)
            if (c is >= '\u3041' and <= '\u3096' or >= '\u30a1' and <= '\u30fa') return true;
        return false;
    }

    private static bool RestoresOmittedGlyphs(Recognition original, Recognition upright)
    {
        if (upright.Text.Length <= original.Text.Length || !ContainsKana(upright.Text) ||
            upright.Confidence < Math.Max(0.85f, original.Confidence - 0.08f))
            return false;

        // 正置候选保留已有字符及其顺序时，允许轻微的置信度差换回漏字。
        int matched = 0;
        foreach (char c in upright.Text)
        {
            if (matched < original.Text.Length && c == original.Text[matched]) matched++;
        }
        return matched == original.Text.Length;
    }

    private static bool PreferCleanedVertical(Recognition original, Recognition cleaned)
    {
        if (cleaned.Text.Length == 0 || cleaned.Text.Length < original.Text.Length) return false;
        if (cleaned.Score > original.Score) return true;
        return cleaned.Confidence >= Math.Max(0.65f, original.Confidence - 0.10f) &&
            CountSmallKana(cleaned.Text) > CountSmallKana(original.Text);
    }

    private static int CountSmallKana(string text)
    {
        int count = 0;
        foreach (char c in text) if (SmallKana.Contains(c)) count++;
        return count;
    }
}
