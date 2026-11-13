using System.Globalization;
using Avalonia;
using Avalonia.Media;
using EhViewerForPc.Core.Imaging;

namespace EhViewerForPc.Controls;

/// <summary>Posts request progress to the UI thread without capturing an
/// arbitrary synchronization context.</summary>
internal sealed class UiImageProgress : IProgress<ImageLoadStatus>
{
    private readonly Action<ImageLoadStatus> _apply;

    public UiImageProgress(Action<ImageLoadStatus> apply) => _apply = apply;

    public void Report(ImageLoadStatus value) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _apply(value), Avalonia.Threading.DispatcherPriority.Background);
}

/// <summary>
/// Draws the loading/error state shared by both image surfaces: a determinate
/// ring when the server sent a length, an indeterminate ring otherwise, and the
/// failure reason when the page could not be loaded. No simulated percentages.
/// </summary>
internal static class ImageStatusOverlay
{
    private static readonly Typeface TextTypeface = new("Microsoft YaHei UI, Segoe UI");

    public static void Draw(
        DrawingContext context,
        Rect bounds,
        ImageLoadStatus status,
        bool failed,
        string? error,
        IBrush mutedBrush,
        IBrush errorBrush)
    {
        bool compact = bounds.Width < 170 || bounds.Height < 130;
        (string Text, string? Detail, bool ShowRing, double Fraction, bool Indeterminate) state =
            Describe(status, failed, error);
        IBrush brush = failed ? errorBrush : mutedBrush;

        // FormattedText.Width is the tight glyph width while TextAlignment=Center
        // places the line inside MaxTextWidth. Drawing at "centre - Width/2"
        // therefore drifts right (and can clip). Anchor on the paragraph box,
        // which is itself centred, so alignment is exact for every string.
        double textWidth = Math.Max(40, bounds.Width - 24);
        double textLeft = bounds.X + (bounds.Width - textWidth) / 2;
        double centerX = bounds.X + bounds.Width / 2;
        double titleSize = compact ? 11 : 12;
        var title = new FormattedText(state.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            TextTypeface, titleSize, brush)
        {
            MaxTextWidth = textWidth,
            TextAlignment = TextAlignment.Center,
        };

        FormattedText? detailText = null;
        if (!compact && state.Detail is { Length: > 0 })
        {
            detailText = new FormattedText(state.Detail, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                TextTypeface, 11, brush)
            {
                MaxTextWidth = textWidth,
                TextAlignment = TextAlignment.Center,
                MaxLineCount = 2,
            };
        }

        double ringDiameter = compact ? 0 : (state.ShowRing ? 34 : 0);
        double blockHeight = title.Height + (detailText is null ? 0 : detailText.Height + 4) + (ringDiameter > 0 ? ringDiameter + 10 : 0);
        double top = bounds.Y + Math.Max(6, (bounds.Height - blockHeight) / 2);

        if (ringDiameter > 0)
        {
            var center = new Point(centerX, top + ringDiameter / 2);
            DrawRing(context, center, ringDiameter / 2, state.Fraction, state.Indeterminate, brush);
            top += ringDiameter + 10;
        }

        context.DrawText(title, new Point(textLeft, top));
        if (detailText is not null)
        {
            context.DrawText(detailText, new Point(textLeft, top + title.Height + 4));
        }
    }

    private static (string Title, string? Detail, bool ShowRing, double Fraction, bool Indeterminate) Describe(
        ImageLoadStatus status,
        bool failed,
        string? error)
    {
        if (failed || status.Phase == ImageLoadPhase.Failed)
        {
            return ("无法加载", error ?? status.Message ?? "请检查网络后重试", false, 0, false);
        }

        switch (status.Phase)
        {
            case ImageLoadPhase.Resolving:
                return ("正在解析页面…", null, false, 0, false);
            case ImageLoadPhase.Downloading when status.HasPercentage:
                return ($"下载 {status.Fraction * 100:F0}%",
                    $"{FormatSize(status.ReceivedBytes)} / {FormatSize(status.TotalBytes!.Value)}",
                    true, status.Fraction, false);
            case ImageLoadPhase.Downloading:
                return (status.ReceivedBytes > 0 ? "下载中" : "等待数据…",
                    status.ReceivedBytes > 0 ? FormatSize(status.ReceivedBytes) : null,
                    true, 0, true);
            case ImageLoadPhase.Decoding:
                return ("解码中…", null, false, 0, false);
            case ImageLoadPhase.Ready:
                return ("就绪", null, false, 0, false);
            default:
                return ("载入中…", null, false, 0, false);
        }
    }

    private static void DrawRing(DrawingContext context, Point center, double radius, double fraction, bool indeterminate, IBrush brush)
    {
        if (indeterminate)
        {
            using (context.PushOpacity(0.7))
            {
                context.DrawEllipse(null, new Pen(brush, 3, new DashStyle([2, 3], 0), PenLineCap.Round), center, radius, radius);
            }

            return;
        }

        // Track.
        using (context.PushOpacity(0.25))
        {
            context.DrawEllipse(null, new Pen(brush, 3, null, PenLineCap.Round), center, radius, radius);
        }

        double sweep = Math.Clamp(fraction, 0, 1) * 359.99;
        if (sweep <= 0.1) return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            var start = new Point(center.X, center.Y - radius);
            ctx.BeginFigure(start, isFilled: false);
            double radians = (sweep - 90) * Math.PI / 180;
            var end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
            ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise);
        }

        context.DrawGeometry(null, new Pen(brush, 3, null, PenLineCap.Round), geometry);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB";
    }
}
