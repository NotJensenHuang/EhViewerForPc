using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using EhViewerForPc.Services;

namespace EhViewerForPc.Controls;

internal static class ReaderTranslationImageReady
{
    public static readonly RoutedEvent<RoutedEventArgs> Event =
        RoutedEvent.Register<Control, RoutedEventArgs>("ReaderTranslationImageReady", RoutingStrategies.Bubble);

    public static void Raise(Control source) => source.RaiseEvent(new RoutedEventArgs(Event));
}

public readonly record struct MangaTranslationOverlayRegion(Rect Bounds, string Translation);

/// <summary>在用于识别的定向图片坐标中排版，绘制时复用阅读器的图像矩形。</summary>
public sealed class MangaTranslationOverlay
{
    private const int MaximumTextElements = 384;
    private readonly MangaTranslationOverlayRegion[] _regions;
    private readonly RegionLayout?[] _layouts;
    private readonly CultureInfo _culture;
    private readonly FlowDirection _flowDirection;
    private readonly Typeface _typeface;

    public MangaTranslationOverlay(
        int width, int height, IReadOnlyList<MangaTranslationOverlayRegion> regions, string targetLanguage = "zh-CN")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _regions = regions.ToArray();
        _layouts = new RegionLayout?[_regions.Length];
        _culture = CultureInfo.GetCultureInfo(targetLanguage);
        _flowDirection = _culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _typeface = new Typeface(_culture.TwoLetterISOLanguageName switch
        {
            "zh" when _culture.Name.Contains("Hant", StringComparison.OrdinalIgnoreCase) ||
                _culture.Name is "zh-TW" or "zh-HK" or "zh-MO" => "Microsoft JhengHei UI, Microsoft JhengHei, Segoe UI",
            "zh" => "Microsoft YaHei UI, Microsoft YaHei, Segoe UI",
            "ja" => "Yu Gothic UI, Yu Gothic, Segoe UI",
            "ko" => "Malgun Gothic, Segoe UI",
            _ => "Segoe UI",
        });
    }

    public int Width { get; }
    public int Height { get; }
    internal FlowDirection TextFlowDirection => _flowDirection;
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
            context.DrawText(layout.Text, layout.Origin);
        }
    }

    private RegionLayout CreateLayout(Rect cover, string translation)
    {
        double padding = Math.Clamp(Math.Min(cover.Width, cover.Height) * 0.08, 0.4, 6);
        var inner = new Rect(cover.X + padding, cover.Y + padding, cover.Width - 2 * padding, cover.Height - 2 * padding);
        string text = LimitText(translation.Trim(), out int textElements);

        // 窄框保留约三个全角字符的行宽，避免放大的单字一行。常规换行由文本排版器处理。
        int charactersPerLine = Math.Min(3, textElements);
        double largest = Math.Max(1, Math.Min(inner.Width / (charactersPerLine + 0.15), inner.Height / 1.35));
        double size = LargestFittingSize(1, largest, fontSize =>
        {
            FormattedText probe = CreateText(text, fontSize, inner.Width);
            return probe.Height <= inner.Height && probe.Width <= inner.Width;
        });

        FormattedText formatted = CreateText(text, size, inner.Width);
        formatted.MaxTextHeight = inner.Height;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        var origin = new Point(inner.X, inner.Y + Math.Max(0, (inner.Height - formatted.Height) / 2));
        return new RegionLayout(cover, formatted, origin);
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

    private FormattedText CreateText(string text, double size, double width) =>
        new(text, _culture, TextFlowDirection, _typeface, size, Brushes.Black)
        {
            MaxTextWidth = width,
            TextAlignment = TextAlignment.Center,
        };

    private static string LimitText(string text, out int textElements)
    {
        int length = 0;
        for (textElements = 0; textElements < MaximumTextElements && length < text.Length; textElements++)
            length += StringInfo.GetNextTextElementLength(text.AsSpan(length));
        return length < text.Length ? string.Concat(text.AsSpan(0, length), "…") : text;
    }

    private sealed record RegionLayout(Rect Bounds, FormattedText Text, Point Origin);
}
