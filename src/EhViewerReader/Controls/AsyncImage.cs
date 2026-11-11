using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EhViewerReader.Services;

namespace EhViewerReader.Controls;

/// <summary>
/// Lazy image surface. Each control owns exactly one decoded bitmap and cancels
/// stale loads when recycled or when its data item changes. This avoids the
/// classic "global LRU disposes a visible bitmap" failure mode.
/// </summary>
public sealed class AsyncImage : Control
{
    public static readonly StyledProperty<IImageRequest?> SourceProperty =
        AvaloniaProperty.Register<AsyncImage, IImageRequest?>(nameof(Source));

    public static readonly StyledProperty<Stretch> StretchProperty =
        AvaloniaProperty.Register<AsyncImage, Stretch>(nameof(Stretch), Stretch.UniformToFill);

    public static readonly StyledProperty<bool> DecodeWidthOnlyProperty =
        AvaloniaProperty.Register<AsyncImage, bool>(nameof(DecodeWidthOnly));

    /// <summary>Play GIF/WebP animations. Library covers keep this off.</summary>
    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<AsyncImage, bool>(nameof(Animate));

    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#20242C"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF6B81"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly Typeface TextTypeface = new("Microsoft YaHei UI, Segoe UI");

    private Bitmap? _bitmap;
    private Bitmap? _retiredBitmap;
    private DecodedImage? _animated;
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private bool _attached;
    private bool _failed;
    private bool _loading;
    private Size _lastLoadSize;
    private string? _lastSourceKey;
    private DispatcherTimer? _detachTimer;

    public AsyncImage()
    {
        ClipToBounds = true;
        SizeChanged += OnSizeChanged;
    }

    public IImageRequest? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool DecodeWidthOnly
    {
        get => GetValue(DecodeWidthOnlyProperty);
        set => SetValue(DecodeWidthOnlyProperty, value);
    }

    public bool Animate
    {
        get => GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    /// <summary>Test hook: the live animation session, if any.</summary>
    internal DecodedImage? AnimatedImageForTest => _animated;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _detachTimer?.Stop();
        _detachTimer = null;
        BeginLoad();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _generation++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _loading = false;

        // ItemsRepeater recycles containers while scrolling. Releasing the bitmap
        // after a short grace period avoids re-decoding the same cover when the
        // container is immediately reused for a nearby item.
        _detachTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _detachTimer.Stop();
        _detachTimer.Tick -= OnDetachTimerTick;
        _detachTimer.Tick += OnDetachTimerTick;
        _detachTimer.Start();

        base.OnDetachedFromVisualTree(e);
    }

    private void OnDetachTimerTick(object? sender, EventArgs e)
    {
        _detachTimer?.Stop();
        if (_attached) return;
        ReleaseBitmaps();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (!IsEffectivelyVisible) { _cancellation?.Cancel(); ReleaseBitmaps(); }
            else BeginLoad();
        }
        else if (change.Property == SourceProperty)
        {
            BeginLoad();
        }
        else if (change.Property == DecodeWidthOnlyProperty || change.Property == StretchProperty)
        {
            InvalidateVisual();
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_attached || Bounds.Width < 8 || Bounds.Height < 4) return;
        if (_bitmap is not null)
        {
            double delta = Math.Abs(e.NewSize.Width - _lastLoadSize.Width);
            if (delta < 24) return;
        }

        BeginLoad();
    }

    private void BeginLoad()
    {
        IImageRequest? request = Source;
        if (!_attached || !IsEffectivelyVisible || request is null || Bounds.Width < 8 || Bounds.Height < 4)
        {
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int targetWidth = Math.Max(1, (int)Math.Round(Bounds.Width * scaling));
        int targetHeight = DecodeWidthOnly
            ? 0
            : Math.Max(1, (int)Math.Round(Bounds.Height * scaling));

        bool sameSource = string.Equals(_lastSourceKey, request.Key, StringComparison.Ordinal);
        if (_bitmap is not null && sameSource &&
            Math.Abs(Bounds.Width - _lastLoadSize.Width) < 24 &&
            Math.Abs(Bounds.Height - _lastLoadSize.Height) < 24)
        {
            return;
        }

        if (!sameSource)
        {
            RetireCurrent();
        }

        _lastSourceKey = request.Key;
        _lastLoadSize = Bounds.Size;
        int generation = ++_generation;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;

        _loading = true;
        _failed = false;
        InvalidateVisual();
        _ = LoadAsync(request, targetWidth, targetHeight, generation, token);
    }

    private async Task LoadAsync(IImageRequest request, int width, int height, int generation, CancellationToken token)
    {
        try
        {
            DecodedImage? image = await request.LoadAsync(width, height, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || generation != _generation)
            {
                image?.Dispose();
                return;
            }

            // Completions are queued at Background priority on purpose: during a
            // fast scroll dozens of decodes finish per frame, and the default
            // Normal priority would preempt input (wheel/scrollbar) and render,
            // starving the UI thread. Slightly later cover arrival is invisible;
            // a frozen scrollbar is not.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _generation)
                {
                    image?.Dispose();
                    return;
                }

                _loading = false;
                Bitmap? previous = _bitmap;
                if (image is null)
                {
                    _bitmap = null;
                    _failed = true;
                    _animated = null;
                }
                else
                {
                    _bitmap = image.Bitmap;
                    _failed = false;
                    StartAnimation(image, generation, token);
                }

                AnimationFramePool.Release(_retiredBitmap);
                _retiredBitmap = previous;
                InvalidateVisual();
            }, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // Expected while scrolling quickly.
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation == _generation)
                {
                    _loading = false;
                    _failed = true;
                    InvalidateVisual();
                }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Frame playback for animated pages in the reader (Animate=true). Library
    /// covers never animate, so a scrolling grid cannot spawn decode storms.
    /// </summary>
    private void StartAnimation(DecodedImage image, int generation, CancellationToken token)
    {
        if (!Animate || !image.IsAnimated || image.FrameCount < 2) return;
        _animated = image;
        _ = AnimationPlayer.PlayAsync(image, async next =>
        {
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _generation || _animated is null)
                {
                    return false;
                }

                Bitmap? old = _bitmap;
                _bitmap = next.Bitmap;
                _animated = next;
                if (old is not null)
                {
                    Dispatcher.UIThread.Post(() => AnimationFramePool.Release(old), DispatcherPriority.Background);
                }

                InvalidateVisual();
                return true;
            });
        }, token);
    }

    private void RetireCurrent()
    {
        AnimationFramePool.Release(_retiredBitmap);
        _retiredBitmap = _bitmap;
        _bitmap = null;
    }

    private void ReleaseBitmaps()
    {
        _generation++;
        _animated = null;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        RetireCurrent();
        AnimationFramePool.Release(_retiredBitmap);
        _retiredBitmap = null;
        _lastSourceKey = null;
        _loading = false;
        _failed = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(PlaceholderBrush, bounds);

        if (_bitmap is null)
        {
            if (_failed)
            {
                DrawStatus(context, bounds, "无法加载", ErrorBrush);
            }
            else if (_loading)
            {
                DrawStatus(context, bounds, "载入中…", MutedTextBrush);
            }

            return;
        }

        var imageSize = new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        if (imageSize.Width <= 0 || imageSize.Height <= 0) return;

        Rect destination = Stretch switch
        {
            Stretch.UniformToFill => UniformToFill(imageSize, bounds),
            Stretch.Fill => bounds,
            Stretch.None => new Rect(imageSize),
            _ => Uniform(imageSize, bounds),
        };

        using (context.PushClip(bounds))
        {
            context.DrawImage(_bitmap, new Rect(imageSize), destination);
        }
    }

    private static Rect Uniform(Size image, Rect bounds)
    {
        double scale = Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
        double width = image.Width * scale;
        double height = image.Height * scale;
        return new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
    }

    private static Rect UniformToFill(Size image, Rect bounds)
    {
        double scale = Math.Max(bounds.Width / image.Width, bounds.Height / image.Height);
        double width = image.Width * scale;
        double height = image.Height * scale;
        return new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
    }

    private static void DrawStatus(DrawingContext context, Rect bounds, string text, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            12,
            brush);
        context.DrawText(formatted, new Point((bounds.Width - formatted.Width) / 2, (bounds.Height - formatted.Height) / 2));
    }
}
