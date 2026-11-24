using System.Globalization;
using Avalonia;
using Avalonia.Media;
using EhViewerForPc.Services;

namespace EhViewerForPc.Controls;

public readonly record struct MangaTranslationOverlayRegion(Rect Bounds, string Translation);

/// <summary>在用于识别的定向图片坐标中排版，绘制时复用阅读器的图像矩形。</summary>
public sealed class MangaTranslationOverlay
{
    private static readonly Typeface TextTypeface = new("Microsoft YaHei UI, Microsoft YaHei, Segoe UI");
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("zh-CN");
    private const int MaximumTextElements = 384;
    private readonly MangaTranslationOverlayRegion[] _regions;
    private readonly RegionLayout?[] _layouts;

    public MangaTranslationOverlay(int width, int height, IReadOnlyList<MangaTranslationOverlayRegion> regions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _regions = regions.ToArray();
        _layouts = new RegionLayout?[_regions.Length];
    }

    public int Width { get; }
    public int Height { get; }
    internal ReaderTranslationImage? ImageSource { get; init; }

    internal bool Matches(ReaderTranslationImage? image) => ImageSource is { } source && source == image;

    internal void Draw(DrawingContext context, Rect image, Rect viewport)
    {
        Rect visible = image.Intersect(viewport);
        if (visible.Width <= 0 || visible.Height <= 0) return;

        double scaleX = image.Width / Width;
        double scaleY = image.Height / Height;
        var visiblePixels = new Rect((visible.X - image.X) / scaleX, (visible.Y - image.Y) / scaleY,
            visible.Width / scaleX, visible.Height / scaleY);
        var imagePixels = new Rect(0, 0, Width, Height);
        using var transformed = context.PushTransform(Matrix.CreateScale(scaleX, scaleY) * Matrix.CreateTranslation(image.X, image.Y));

        for (int i = 0; i < _regions.Length; i++)
        {
            MangaTranslationOverlayRegion region = _regions[i];
            Rect cover = region.Bounds.Intersect(imagePixels);
            if (cover.Width < 3 || cover.Height < 3 || !cover.Intersects(visiblePixels) ||
                string.IsNullOrWhiteSpace(region.Translation)) continue;

            // 这是漫画画面的一部分，固定纸白与墨黑使内容在应用的两种主题下保持一致。
            context.FillRectangle(Brushes.White, cover);
            _layouts[i] ??= CreateLayout(cover, region.Translation);
        }

        // 先遮住全部识别区域，再绘制文字，避免相邻区域边界重叠时擦掉已绘制的译文。
        foreach (RegionLayout? layout in _layouts)
        {
            if (layout is null || !layout.Bounds.Intersects(visiblePixels)) continue;
            using var clip = context.PushClip(layout.Bounds);
            foreach (TextRun run in layout.Runs) context.DrawText(run.Text, run.Origin);
        }
    }

    private RegionLayout CreateLayout(Rect cover, string translation)
    {
        double padding = Math.Clamp(Math.Min(cover.Width, cover.Height) * 0.08, 0.4, 6);
        var inner = new Rect(cover.X + padding, cover.Y + padding, cover.Width - 2 * padding, cover.Height - 2 * padding);
        double preferredMinimum = Math.Clamp(Math.Max(Width, Height) / 125d, 7, 17);
        string text = LimitText(translation.Trim());
        TextRun[] runs = inner.Height > inner.Width * 1.5
            ? LayoutVertical(inner, text, preferredMinimum)
            : [LayoutHorizontal(inner, text, preferredMinimum)];
        return new RegionLayout(cover, runs);
    }

    private static TextRun LayoutHorizontal(Rect bounds, string text, double preferredMinimum)
    {
        double largest = Math.Max(1, Math.Min(bounds.Width, bounds.Height / 1.35));
        double smallest = Math.Min(largest, preferredMinimum);
        double size = LargestFittingSize(smallest, largest, fontSize =>
        {
            FormattedText probe = CreateText(text, fontSize, bounds.Width);
            return probe.Height <= bounds.Height && probe.Width <= bounds.Width;
        });

        FormattedText formatted = CreateText(text, size, bounds.Width);
        formatted.MaxTextHeight = bounds.Height;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        return new TextRun(formatted, new Point(bounds.X, bounds.Y + Math.Max(0, (bounds.Height - formatted.Height) / 2)));
    }

    private static TextRun[] LayoutVertical(Rect bounds, string text, double preferredMinimum)
    {
        var elements = new List<string>();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element)) elements.Add(element);
        }

        double lineHeight = CreateText("中", 10, 20).Height / 10;
        const double columnWidth = 1.15;
        double largest = Math.Max(1, Math.Min(bounds.Width / columnWidth, bounds.Height / lineHeight));
        double smallest = Math.Min(largest, preferredMinimum);
        double size = LargestFittingSize(smallest, largest, fontSize =>
            Math.Floor(bounds.Height / (fontSize * lineHeight)) *
            Math.Floor(bounds.Width / (fontSize * columnWidth)) >= elements.Count);

        double cellHeight = size * lineHeight;
        double cellWidth = size * columnWidth;
        int rows = Math.Max(1, (int)Math.Floor(bounds.Height / cellHeight));
        int columnCapacity = Math.Max(1, (int)Math.Floor(bounds.Width / cellWidth));
        int visible = Math.Min(elements.Count, rows * columnCapacity);
        if (visible == 0) return [];

        int columns = (visible + rows - 1) / rows;
        double right = bounds.X + (bounds.Width + columns * cellWidth) / 2;
        double top = bounds.Y + (bounds.Height - Math.Min(visible, rows) * cellHeight) / 2;
        var runs = new TextRun[visible];
        for (int i = 0; i < runs.Length; i++)
        {
            string glyph = i == visible - 1 && visible < elements.Count ? "…" : elements[i];
            var origin = new Point(right - (i / rows + 1) * cellWidth, top + i % rows * cellHeight);
            runs[i] = new TextRun(CreateText(glyph, size, cellWidth), origin);
        }

        return runs;
    }

    private static double LargestFittingSize(double minimum, double maximum, Func<double, bool> fits)
    {
        if (fits(maximum)) return maximum;
        for (int attempt = 0; attempt < 7 && maximum - minimum > 0.2; attempt++)
        {
            double middle = (minimum + maximum) / 2;
            if (fits(middle)) minimum = middle;
            else maximum = middle;
        }

        return minimum;
    }

    private static FormattedText CreateText(string text, double size, double width) =>
        new(text, Culture, FlowDirection.LeftToRight, TextTypeface, size, Brushes.Black)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment.Center,
        };

    private static string LimitText(string text)
    {
        int length = 0;
        for (int i = 0; i < MaximumTextElements && length < text.Length; i++)
            length += StringInfo.GetNextTextElementLength(text.AsSpan(length));
        return length < text.Length ? string.Concat(text.AsSpan(0, length), "…") : text;
    }

    private sealed record RegionLayout(Rect Bounds, TextRun[] Runs);
    private readonly record struct TextRun(FormattedText Text, Point Origin);
}
