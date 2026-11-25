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
        token.ThrowIfCancellationRequested();
        using SKBitmap bitmap = DecodeImage(imagePath);
        int width = bitmap.Width;
        int height = bitmap.Height;
        var pixels = new ImagePixels(bitmap.GetPixelSpan(), width, height, bitmap.RowBytes, 4);
        string workingDirectory = Path.GetDirectoryName(imagePath)!;
        using var ort = new MangaOcrOrt(runtimeDirectory);
        List<Quad> boxes;
        using (var detector = ort.Open(Path.Combine(modelDirectory, "det.onnx"), Path.Combine(workingDirectory, "det-profile")))
        {
            double scale = Math.Min(1.0, (double)MaxDetectionDimension / Math.Max(width, height));
            int inputWidth = Math.Max(32, (int)Math.Round(width * scale / 32) * 32);
            int inputHeight = Math.Max(32, (int)Math.Round(height * scale / 32) * 32);
            float[] data = PrepareDetection(pixels, inputWidth, inputHeight, token);
            boxes = detector.Run(data, inputWidth, inputHeight,
                (probability, shape) => DetectBoxes(probability, shape, width, height, token));
        }
        token.ThrowIfCancellationRequested();
        if (boxes.Count == 0) return new MangaOcrResult(width, height, [], MangaOcrOrt.Provider);

        string[] dictionary = LoadDictionary(Path.Combine(modelDirectory, "rec-dict.txt"));
        using var recognizer = ort.Open(Path.Combine(modelDirectory, "rec.onnx"), Path.Combine(workingDirectory, "rec-profile"));
        var regions = new List<MangaOcrRegion>(boxes.Count);
        foreach (Quad box in boxes)
        {
            token.ThrowIfCancellationRequested();
            Crop crop = CropRegion(pixels, box, token);
            Recognition reading = RecognizeLine(crop, recognizer, dictionary, token);
            if (reading.Confidence < MinimumRecognitionConfidence)
                reading = RecoverVerticalColumns(crop, recognizer, dictionary, token);
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
        return new MangaOcrResult(width, height, ArrangeDialogue(regions, bitmap.GetPixelSpan(), width, height, bitmap.RowBytes, token), MangaOcrOrt.Provider);
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

    private static Recognition RecognizeLine(Crop crop, MangaOcrOrt.Session recognizer, string[] dictionary,
        CancellationToken token, bool cleaned = false)
    {
        if (crop.Height <= crop.Width * 1.5) return RecognizeCrop(crop, recognizer, dictionary, token);
        Recognition best = RecognizeCrop(RotateVertical(crop, clockwise: false), recognizer, dictionary, token);
        bool strong = best.Confidence >= 0.85f && ContainsKana(best.Text);
        if (strong && !cleaned) return best;
        if (!strong)
        {
            Recognition reversed = RecognizeCrop(RotateVertical(crop, clockwise: true), recognizer, dictionary, token);
            if (reversed.Score > best.Score) best = reversed;
        }
        if (LayOutVerticalGlyphs(crop) is { } horizontal)
        {
            Recognition reading = RecognizeCrop(horizontal, recognizer, dictionary, token);
            if (cleaned ? ContainsKana(reading.Text) && PreferCleanedVertical(best, reading) : reading.Score > best.Score)
                best = reading;
        }
        if (!cleaned && CleanBrightVerticalGlyphs(crop) is { } cleanedGlyphs)
        {
            Recognition reading = RecognizeCrop(cleanedGlyphs, recognizer, dictionary, token);
            if (PreferCleanedVertical(best, reading)) best = reading;
        }
        return best;
    }

    private static Recognition RecoverVerticalColumns(Crop crop, MangaOcrOrt.Session recognizer,
        string[] dictionary, CancellationToken token)
    {
        if (SplitVerticalColumns(crop, token) is not { } columns) return new Recognition("", 0);
        // 一次检测把相邻竖列框到一起时，逐列识别，仍以原检测框提交一次翻译。
        Recognition right = RecognizeLine(columns.Right, recognizer, dictionary, token, cleaned: true);
        if (right.Confidence < 0.65f || !ContainsKana(right.Text)) return new Recognition("", 0);
        Recognition left = RecognizeLine(columns.Left, recognizer, dictionary, token, cleaned: true);
        if (left.Confidence < 0.65f || !ContainsKana(left.Text)) return new Recognition("", 0);
        return new Recognition(string.Concat(right.Text, left.Text), Math.Min(right.Confidence, left.Confidence));
    }

    private static Recognition RecognizeCrop(Crop crop, MangaOcrOrt.Session recognizer, string[] dictionary, CancellationToken token)
    {
        int contentWidth = Math.Clamp((int)Math.Round((double)RecognitionHeight * crop.Width / crop.Height), 1, MaxDetectionDimension);
        int inputWidth = Math.Max(64, (contentWidth + 31) / 32 * 32);
        float[] data = PrepareRecognition(crop, contentWidth, inputWidth, token);
        return recognizer.Run(data, inputWidth, RecognitionHeight,
            (probability, shape) => DecodeCtc(probability, shape, dictionary, token));
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
