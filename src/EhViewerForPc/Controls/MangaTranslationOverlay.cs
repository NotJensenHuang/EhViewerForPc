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

public readonly record struct MangaTranslationOverlayRegion(Rect Bounds, string Translation)
{
    internal Rect? SafeTextBounds { get; init; }
    internal Rect? BalancedTextBounds { get; init; }
    internal Rect[]? SourceMasks { get; init; }
}

/// <summary>在用于识别的定向图片坐标中排版，绘制时复用阅读器的图像矩形。</summary>
public sealed class MangaTranslationOverlay
{
    internal const double MinimumDisplayFontSize = 12;
    private const int MaximumTextElements = 384;
    private const double CaptionPadding = 5;
    private const double MinimumCjkCaptionWidth = 4 * MinimumDisplayFontSize + 2 * CaptionPadding;
    private static readonly Pen CaptionBorder = new(Brushes.Black, 1);
    private readonly CaptionCache[] _captions;
    private readonly List<Rect> _occupied;
    private readonly MangaTranslationOverlayRegion[] _regions;
    private readonly RegionLayout?[] _layouts;
    private readonly bool[] _layoutMeasured;
    private readonly Rect[] _textBounds;
    private readonly Rect[] _visibleSources;
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
        _layoutMeasured = new bool[_regions.Length];
        _visibleSources = new Rect[_regions.Length];
        _captions = new CaptionCache[_regions.Length];
        _occupied = new List<Rect>(_regions.Length * 2);
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
        _textBounds = ChooseTextBounds(new Rect(0, 0, width, height));
    }

    public int Width { get; }
    public int Height { get; }
    internal FlowDirection TextFlowDirection => _flowDirection;
    internal ReaderTranslationImage? ImageSource { get; init; }
    internal Rect[] ProtectedImageBounds { get; init; } = [];

    internal bool Matches(ReaderTranslationImage? image) => ImageSource is { } source && source == image;

    internal void Draw(DrawingContext context, Rect image, Rect viewport)
    {
        Rect visible = image.Intersect(viewport);
        if (visible.Width <= 0 || visible.Height <= 0) return;

        double scaleX = image.Width / Width;
        double scaleY = image.Height / Height;
        double displayScale = Math.Min(scaleX, scaleY);
        var imagePixels = new Rect(0, 0, Width, Height);
        var visiblePixels = new Rect((visible.X - image.X) / scaleX, (visible.Y - image.Y) / scaleY,
            visible.Width / scaleX, visible.Height / scaleY);
        Rect ToScreen(Rect rect) => new(image.X + rect.X * scaleX, image.Y + rect.Y * scaleY,
            rect.Width * scaleX, rect.Height * scaleY);

        _occupied.Clear();
        foreach (Rect protectedBounds in ProtectedImageBounds)
        {
            Rect visibleBounds = ToScreen(protectedBounds).Intersect(visible);
            if (visibleBounds.Width > 0 && visibleBounds.Height > 0) _occupied.Add(visibleBounds);
        }
        for (int i = 0; i < _regions.Length; i++)
        {
            Rect source = _regions[i].Bounds.Intersect(imagePixels);
            Rect text = _textBounds[i];
            if (!source.Intersects(visiblePixels) && !text.Intersects(visiblePixels)) continue;
            RegionLayout? layout = MeasureLayout(i, source);
            if (layout is not null && layout.FontSize * displayScale >= MinimumDisplayFontSize)
            {
                // 气泡外的标签需避开原本已经可读的译文与其原字遮罩。
                _occupied.Add(ToScreen(layout.Bounds).Intersect(visible));
                _occupied.Add(ToScreen(source).Intersect(visible));
            }
        }

        using var clipToView = context.PushClip(visible);
        using (context.PushTransform(Matrix.CreateScale(scaleX, scaleY) * Matrix.CreateTranslation(image.X, image.Y)))
        {
            for (int i = 0; i < _regions.Length; i++)
            {
                RegionLayout? layout = _layouts[i];
                if (!_layoutMeasured[i] || layout is null || layout.FontSize * displayScale < MinimumDisplayFontSize) continue;
                MangaTranslationOverlayRegion region = _regions[i];
                Rect cover = region.Bounds.Intersect(imagePixels).Intersect(visiblePixels);
                if (cover.Width <= 0 || cover.Height <= 0) continue;

                if (layout.Bounds != region.Bounds && region.SourceMasks is { Length: > 0 } sourceMasks)
                {
                    foreach (Rect mask in sourceMasks)
                    {
                        Rect clipped = mask.Intersect(cover);
                        if (clipped.Width > 0 && clipped.Height > 0) context.FillRectangle(Brushes.White, clipped);
                    }
                }
                else
                {
                    context.FillRectangle(Brushes.White, cover);
                }
            }

            for (int i = 0; i < _layouts.Length; i++)
            {
                RegionLayout? layout = _layouts[i];
                if (!_layoutMeasured[i] || layout is null || layout.FontSize * displayScale < MinimumDisplayFontSize ||
                    !layout.Bounds.Intersects(visiblePixels)) continue;
                using var clip = context.PushClip(layout.Bounds);
                context.DrawText(layout.Text, layout.Origin);
            }
        }

        Rect available = visible.Deflate(new Thickness(2));
        if (available.Width <= 2 * CaptionPadding || available.Height <= 2 * CaptionPadding) return;
        for (int i = 0; i < _visibleSources.Length; i++)
            _visibleSources[i] = ToScreen(_regions[i].Bounds).Intersect(visible);

        for (int i = 0; i < _regions.Length; i++)
        {
            // 气泡外标签以屏幕 DIP 绘制，缩放和平移只改变锚点和可用空间。
            RegionLayout? layout = _layouts[i];
            if (layout is not null && layout.FontSize * displayScale >= MinimumDisplayFontSize) continue;
            Rect anchor = ToScreen(_textBounds[i]).Intersect(visible);
            if (anchor.Width <= 0 || anchor.Height <= 0) continue;
            CaptionLayout? caption = null;
            Rect box = default;
            double spill = double.PositiveInfinity;
            foreach (CaptionLayout candidate in MeasureCaptions(i, available.Size, anchor.Width))
            {
                if (candidate.Compact && caption is { Compact: false }) break;
                if (FindCaptionPosition(anchor, candidate.Size, available, _visibleSources, i) is not { } placement) continue;
                Rect overlap = placement.Intersect(anchor);
                double outside = placement.Width * placement.Height - overlap.Width * overlap.Height;
                if (outside >= spill) continue;
                spill = outside;
                box = placement;
                caption = candidate;
            }
            if (caption is null) continue;

            _occupied.Add(box);
            context.DrawRectangle(Brushes.White, CaptionBorder, box, 3, 3);
            context.DrawText(caption.Text, new Point(box.X + CaptionPadding, box.Y + CaptionPadding));
        }
    }

    private RegionLayout? MeasureLayout(int index, Rect source)
    {
        if (_layoutMeasured[index]) return _layouts[index];
        Rect textBounds = _textBounds[index];
        string translation = _regions[index].Translation;
        RegionLayout? best = _layouts[index] is { } measured && measured.Bounds == textBounds
            ? measured : CreateLayout(textBounds, translation);
        if (textBounds != source)
        {
            RegionLayout? sourceLayout = CreateLayout(source, translation);
            if (sourceLayout is not null && (best is null || sourceLayout.FontSize > best.FontSize)) best = sourceLayout;
        }
        _layouts[index] = best;
        _layoutMeasured[index] = true;
        return best;
    }

    private CaptionLayout[] MeasureCaptions(int index, Size space, double anchorWidth)
    {
        // 避免为了少量外扩把正常对白挤成单字一行。短句仍按实际文字宽度收紧。
        bool isCjk = _culture.TwoLetterISOLanguageName is "zh" or "ja" or "ko";
        double minimum = Math.Min(space.Width, isCjk ? MinimumCjkCaptionWidth : 24);
        double width = Math.Clamp(Math.Ceiling(anchorWidth / 4) * 4, minimum, space.Width);
        if (_captions[index] is { Layouts: { } cached } cache && cache.Space == space && cache.Width == width)
            return cached;

        string translation = _regions[index].Translation.Trim();
        var layouts = new List<CaptionLayout>(10);
        if (FitsTextBudget(translation))
        {
            double previous = 0;
            foreach (double option in (ReadOnlySpan<double>)[width * 0.75, width, width * 1.35, width * 1.85,
                         width * 2.6, width * 3.6, width * 5, space.Width])
            {
                double extent = Math.Min(space.Width, Math.Max(minimum, option));
                if (extent <= previous) continue;
                previous = extent;
                if (MeasureCaption(translation, extent, space.Height, MinimumDisplayFontSize) is { } layout)
                    layouts.Add(layout);
            }

            // 页缘或邻字挡住常规宽度时，仍可尝试容纳两个或三个全角字的窄排。
            if (isCjk)
            {
                for (int columns = 2; columns <= 3; columns++)
                {
                    double extent = columns * MinimumDisplayFontSize + 2 * CaptionPadding;
                    if (extent < minimum && MeasureCaption(translation, extent, space.Height, MinimumDisplayFontSize) is { } layout)
                        layouts.Add(layout with { Compact = true });
                }
            }
        }
        var result = layouts.ToArray();
        _captions[index] = new CaptionCache(space, width, result);
        return result;
    }

    private CaptionLayout? MeasureCaption(string text, double width, double height, double fontSize)
    {
        double contentWidth = width - 2 * CaptionPadding;
        FormattedText formatted = CreateText(text, fontSize, contentWidth);
        if (formatted.Width > contentWidth || formatted.Height + 2 * CaptionPadding > height) return null;
        if (formatted.Width + 0.5 < contentWidth)
        {
            // 短译文不必占用整块预留宽度，也可为相邻气泡留下位置。
            FormattedText narrower = CreateText(text, fontSize, formatted.Width + 0.5);
            if (narrower.Height <= formatted.Height)
            {
                width = formatted.Width + 0.5 + 2 * CaptionPadding;
                formatted = narrower;
            }
        }
        return new CaptionLayout(new Size(width, formatted.Height + 2 * CaptionPadding), formatted);
    }

    private Rect? FindCaptionPosition(Rect anchor, Size size, Rect available,
        ReadOnlySpan<Rect> sourceBounds, int owner)
    {
        double right = available.Right - size.Width;
        double bottom = available.Bottom - size.Height;
        Span<double> xs = stackalloc double[]
        {
            anchor.Center.X - size.Width / 2, anchor.Left, anchor.Right - size.Width, available.Left, right,
            anchor.Left - size.Width * 0.65, anchor.Right - size.Width * 0.35,
        };
        Span<double> ys = stackalloc double[]
        {
            anchor.Center.Y - size.Height / 2, anchor.Top, anchor.Bottom - size.Height, available.Top, bottom,
            anchor.Top - size.Height * 0.65, anchor.Bottom - size.Height * 0.35,
        };
        Rect? best = null;
        double bestOverlap = 0;
        double bestDistance = double.PositiveInfinity;
        double minimumOverlap = Math.Min(anchor.Width * anchor.Height, size.Width * size.Height) * 0.2;
        foreach (double y in ys)
        {
            foreach (double x in xs)
            {
                var box = new Rect(new Point(Math.Clamp(x, available.Left, right), Math.Clamp(y, available.Top, bottom)), size);
                Rect intersection = box.Intersect(anchor);
                double overlap = intersection.Width * intersection.Height;
                Vector offset = box.Center - anchor.Center;
                double distance = offset.X * offset.X + offset.Y * offset.Y;
                if (overlap < minimumOverlap || overlap < bestOverlap ||
                    (overlap == bestOverlap && distance >= bestDistance)) continue;
                bool collision = false;
                foreach (Rect occupied in _occupied)
                {
                    if (!box.Intersects(occupied)) continue;
                    collision = true;
                    break;
                }
                for (int i = 0; !collision && i < sourceBounds.Length; i++)
                {
                    if (i != owner && box.Intersects(sourceBounds[i])) collision = true;
                }
                if (collision) continue;
                best = box;
                bestOverlap = overlap;
                bestDistance = distance;
            }
        }
        return best;
    }

    private Rect[] ChooseTextBounds(Rect image)
    {
        var bounds = new Rect[_regions.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            MangaTranslationOverlayRegion region = _regions[i];
            Rect source = region.Bounds.Intersect(image);
            bounds[i] = source;
            Rect? wide = ValidateCandidate(region.SafeTextBounds, image, source);
            Rect? balanced = ValidateCandidate(region.BalancedTextBounds, image, source);
            bounds[i] = wide ?? balanced ?? source;
            if (wide is { } first && balanced is { } second && first != second)
            {
                RegionLayout? wideLayout = CreateLayout(first, region.Translation);
                RegionLayout? balancedLayout = CreateLayout(second, region.Translation);
                if (balancedLayout is not null && (wideLayout is null || balancedLayout.FontSize > wideLayout.FontSize + 0.25))
                {
                    bounds[i] = second;
                    _layouts[i] = balancedLayout;
                }
                else _layouts[i] = wideLayout;
            }
        }

        for (int i = 0; i < bounds.Length; i++)
        {
            for (int j = i + 1; j < bounds.Length; j++)
            {
                Rect overlap = bounds[i].Intersect(bounds[j]);
                if (overlap.Width <= 0 || overlap.Height <= 0) continue;
                Rect first = _regions[i].Bounds.Intersect(image);
                Rect second = _regions[j].Bounds.Intersect(image);
                Rect? left = SpaceBeside(bounds[i], first, second, bounds[j]);
                Rect? right = SpaceBeside(bounds[j], second, first, bounds[i]);
                bounds[i] = left ?? first;
                bounds[j] = right ?? second;
                overlap = bounds[i].Intersect(bounds[j]);
                if (overlap.Width > 0 && overlap.Height > 0)
                {
                    // 两区选择了不同的分隔轴时，用同一轴切开共享空间。
                    if (first.Right <= second.Left || second.Right <= first.Left)
                    {
                        double split = first.Right <= second.Left
                            ? (first.Right + second.Left) / 2 : (second.Right + first.Left) / 2;
                        bounds[i] = ClipAtX(bounds[i], split, first.Center.X < split);
                        bounds[j] = ClipAtX(bounds[j], split, second.Center.X < split);
                    }
                    else if (first.Bottom <= second.Top || second.Bottom <= first.Top)
                    {
                        double split = first.Bottom <= second.Top
                            ? (first.Bottom + second.Top) / 2 : (second.Bottom + first.Top) / 2;
                        bounds[i] = ClipAtY(bounds[i], split, first.Center.Y < split);
                        bounds[j] = ClipAtY(bounds[j], split, second.Center.Y < split);
                    }
                }
            }
        }
        return bounds;
    }

    private static Rect? ValidateCandidate(Rect? candidate, Rect image, Rect source) =>
        candidate is { Width: >= 3, Height: >= 3 } rect && image.Contains(rect) && rect.Intersects(source) ? rect : null;

    internal static Rect? SpaceBeside(Rect area, Rect source, Rect obstacle, Rect? shared = null)
    {
        Rect overlap = area.Intersect(shared ?? obstacle);
        if (overlap.Width <= 0 || overlap.Height <= 0) return area;

        Rect best = default;
        void Consider(Rect candidate)
        {
            if (candidate.Width >= 3 && candidate.Height >= 3 && candidate.Contains(source.Center) &&
                candidate.Width * candidate.Height > best.Width * best.Height)
                best = candidate;
        }

        if (source.Right <= obstacle.Left)
            Consider(ClipAtX(area, shared is null ? obstacle.Left : (source.Right + obstacle.Left) / 2, keepLeft: true));
        if (source.Left >= obstacle.Right)
            Consider(ClipAtX(area, shared is null ? obstacle.Right : (source.Left + obstacle.Right) / 2, keepLeft: false));
        if (source.Bottom <= obstacle.Top)
            Consider(ClipAtY(area, shared is null ? obstacle.Top : (source.Bottom + obstacle.Top) / 2, keepTop: true));
        if (source.Top >= obstacle.Bottom)
            Consider(ClipAtY(area, shared is null ? obstacle.Bottom : (source.Top + obstacle.Bottom) / 2, keepTop: false));

        return best.Width > 0 ? best : null;
    }

    private static Rect ClipAtX(Rect rect, double split, bool keepLeft)
    {
        double edge = Math.Clamp(split, rect.Left, rect.Right);
        return keepLeft ? new Rect(rect.Left, rect.Top, edge - rect.Left, rect.Height)
            : new Rect(edge, rect.Top, rect.Right - edge, rect.Height);
    }

    private static Rect ClipAtY(Rect rect, double split, bool keepTop)
    {
        double edge = Math.Clamp(split, rect.Top, rect.Bottom);
        return keepTop ? new Rect(rect.Left, rect.Top, rect.Width, edge - rect.Top)
            : new Rect(rect.Left, edge, rect.Width, rect.Bottom - edge);
    }

    private RegionLayout? CreateLayout(Rect bounds, string translation)
    {
        if (bounds.Width < 3 || bounds.Height < 3 || string.IsNullOrWhiteSpace(translation)) return null;
        string text = translation.Trim();
        if (!FitsTextBudget(text)) return null;

        double padding = Math.Clamp(Math.Min(bounds.Width, bounds.Height) * 0.08, 0.4, 6);
        var inner = new Rect(bounds.X + padding, bounds.Y + padding, bounds.Width - 2 * padding, bounds.Height - 2 * padding);
        double largest = Math.Min(inner.Width, inner.Height);
        bool Fits(double size)
        {
            FormattedText probe = CreateText(text, size, inner.Width);
            return probe.Height <= inner.Height && probe.Width <= inner.Width;
        }

        if (largest < 1 || !Fits(1)) return null;
        double fontSize = LargestFittingSize(1, largest, Fits);
        FormattedText formatted = CreateText(text, fontSize, inner.Width);
        var origin = new Point(inner.X, inner.Y + (inner.Height - formatted.Height) / 2);
        return new RegionLayout(bounds, formatted, origin, fontSize);
    }

    private static double LargestFittingSize(double minimum, double maximum, Func<double, bool> fits)
    {
        if (fits(maximum)) return maximum;
        for (int attempt = 0; attempt < 12 && maximum - minimum > 0.2; attempt++)
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

    private static bool FitsTextBudget(string text)
    {
        int length = 0;
        for (int i = 0; i < MaximumTextElements && length < text.Length; i++)
            length += StringInfo.GetNextTextElementLength(text.AsSpan(length));
        return length == text.Length;
    }

    private sealed record CaptionLayout(Size Size, FormattedText Text)
    {
        public bool Compact { get; init; }
    }
    private readonly record struct CaptionCache(Size Space, double Width, CaptionLayout[] Layouts);

    private sealed record RegionLayout(Rect Bounds, FormattedText Text, Point Origin, double FontSize);
}
