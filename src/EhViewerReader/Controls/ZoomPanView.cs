using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EhViewerReader.Core.Models;
using EhViewerReader.Services;

namespace EhViewerReader.Controls;

public sealed class PageTurnEventArgs : EventArgs
{
    public PageTurnEventArgs(int delta) => Delta = delta;
    public int Delta { get; }
}

/// <summary>
/// Single-page zoom/pan surface that owns its decoded bitmap. The reader only
/// supplies an <see cref="IImageRequest"/>; loading, cancellation, disposal and
/// next-page preloading all happen here so no bitmap crosses UI boundaries.
/// </summary>
public sealed class ZoomPanView : Control
{
    private const double MinZoom = 1.0;
    private const double MaxZoom = 8.0;
    private const double DragThreshold = 4.0;

    public static readonly StyledProperty<IImageRequest?> RequestProperty =
        AvaloniaProperty.Register<ZoomPanView, IImageRequest?>(nameof(Request));

    public static readonly StyledProperty<IImageRequest?> PreloadRequestProperty =
        AvaloniaProperty.Register<ZoomPanView, IImageRequest?>(nameof(PreloadRequest));

    public static readonly StyledProperty<FitMode> FitModeProperty =
        AvaloniaProperty.Register<ZoomPanView, FitMode>(nameof(FitMode), FitMode.FitPage);

    public static readonly StyledProperty<bool> RightToLeftProperty =
        AvaloniaProperty.Register<ZoomPanView, bool>(nameof(RightToLeft), true);

    public static readonly StyledProperty<int> MaxMegapixelsProperty =
        AvaloniaProperty.Register<ZoomPanView, int>(nameof(MaxMegapixels), 24);

    public static readonly StyledProperty<int> MaxDimensionProperty =
        AvaloniaProperty.Register<ZoomPanView, int>(nameof(MaxDimension), 16000);

    private static readonly IBrush PlaceholderBrush = new SolidColorBrush(Color.Parse("#0F1216"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#8A93A6"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF6B81"));
    private static readonly Typeface TextTypeface = new("Microsoft YaHei UI, Segoe UI");

    private Bitmap? _bitmap;
    private Bitmap? _retiredBitmap;
    private DecodedImage? _preloaded;
    private string? _preloadedKey;
    private DecodedImage? _animated;
    private Task<string>? _animationTask;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _preloadCancellation;
    private int _generation;
    private int _preloadGeneration;
    private bool _attached;
    private bool _loading;
    private bool _failed;
    private string? _lastLoadKey;
    private int _lastTargetWidth;
    private int _lastTargetHeight;
    private int _decodedZoomFactor = 1;
    private Size _lastSize;
    private double _zoom = 1.0;
    private double _offsetX;
    private double _offsetY;
    private bool _pointerCaptured;
    private bool _dragged;
    private Point _pressPoint;
    private Point _lastPoint;
    private DispatcherTimer? _zoomReloadTimer;

    public ZoomPanView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public event EventHandler<PageTurnEventArgs>? PageTurnRequested;

    public IImageRequest? Request
    {
        get => GetValue(RequestProperty);
        set => SetValue(RequestProperty, value);
    }

    public IImageRequest? PreloadRequest
    {
        get => GetValue(PreloadRequestProperty);
        set => SetValue(PreloadRequestProperty, value);
    }

    public FitMode FitMode
    {
        get => GetValue(FitModeProperty);
        set => SetValue(FitModeProperty, value);
    }

    public bool RightToLeft
    {
        get => GetValue(RightToLeftProperty);
        set => SetValue(RightToLeftProperty, value);
    }

    public int MaxMegapixels
    {
        get => GetValue(MaxMegapixelsProperty);
        set => SetValue(MaxMegapixelsProperty, value);
    }

    public int MaxDimension
    {
        get => GetValue(MaxDimensionProperty);
        set => SetValue(MaxDimensionProperty, value);
    }

    public double CurrentZoom => _zoom;
    public bool IsLoading => _loading;
    public bool HasImage => _bitmap is not null;

    /// <summary>True while an animation session is attached to the current page.</summary>
    internal bool HasAnimation => _animated is not null;

    /// <summary>Test hook: the live animation session (its FrameIndex advances).</summary>
    internal DecodedImage? AnimatedImageForTest => _animated;

    /// <summary>Test hook: diagnostic reason why the last playback loop ended.</summary>
    internal string? AnimationStopReason { get; private set; }

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
        BeginLoad(resetView: false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _zoomReloadTimer?.Stop();
        CancelLoads();
        Bitmap? current = _bitmap;
        Bitmap? retired = _retiredBitmap;
        DecodedImage? preloaded = _preloaded;
        _bitmap = null;
        _retiredBitmap = null;
        _preloaded = null;
        _preloadedKey = null;
        // The render thread may still hold a reference for one frame.
        Dispatcher.UIThread.Post(() =>
        {
            // Pool-backed animation frames must go back to the pool, not the GC.
            AnimationFramePool.Release(current);
            AnimationFramePool.Release(retired);
            preloaded?.Dispose();
        }, DispatcherPriority.Background);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            if (!IsEffectivelyVisible) { _cancellation?.Cancel(); CancelLoads(); RetireCurrent(); AnimationFramePool.Release(_retiredBitmap); _retiredBitmap = null; _preloaded?.Dispose(); _preloaded = null; _preloadedKey = null; }
            else BeginLoad(resetView: false);
        }
        else if (change.Property == RequestProperty)
        {
            OnRequestChanged();
        }
        else if (change.Property == FitModeProperty)
        {
            _zoom = 1;
            _offsetX = 0;
            _offsetY = 0;
            _decodedZoomFactor = 1;
            BeginLoad(resetView: true);
            InvalidateVisual();
        }
        else if (change.Property == PreloadRequestProperty)
        {
            TryPreload();
        }
        else if (change.Property == RightToLeftProperty)
        {
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_zoom <= MinZoom + 0.001)
        {
            _offsetX = 0;
            _offsetY = 0;
        }
        else
        {
            ClampOffsets();
        }

        if (!_attached) return;
        bool significant = _bitmap is null ||
                           Math.Abs(e.NewSize.Width - _lastSize.Width) > 24 ||
                           Math.Abs(e.NewSize.Height - _lastSize.Height) > 24;
        if (significant)
        {
            BeginLoad(resetView: false);
        }

        InvalidateVisual();
    }

    private void OnRequestChanged()
    {
        _zoom = 1;
        _offsetX = 0;
        _offsetY = 0;
        _decodedZoomFactor = 1;
        CancelLoads();
        RetireCurrent();

        IImageRequest? request = Request;
        if (request is not null && _preloaded is not null &&
            string.Equals(_preloadedKey, request.Key, StringComparison.Ordinal))
        {
            _bitmap = _preloaded.Bitmap;
            DecodedImage adopted = _preloaded;
            _preloaded = null;
            _preloadedKey = null;
            _loading = false;
            _failed = false;
            StartAnimation(adopted, _generation, CancellationToken.None);
            InvalidateVisual();
            TryPreload();
            return;
        }

        _lastLoadKey = null;
        BeginLoad(resetView: true);
    }

    private void RetireCurrent()
    {
        AnimationFramePool.Release(_retiredBitmap);
        _retiredBitmap = _bitmap;
        _bitmap = null;
    }

    private void BeginLoad(bool resetView)
    {
        IImageRequest? request = Request;
        if (!_attached || !IsEffectivelyVisible || request is null || Bounds.Width < 8 || Bounds.Height < 8)
        {
            InvalidateVisual();
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        (int targetWidth, int targetHeight) = ComputeTargetSize(scaling, _decodedZoomFactor);
        if (targetWidth <= 0 || targetHeight <= 0) return;

        string key = request.Key + "|" + _decodedZoomFactor;
        if (!resetView && _bitmap is not null &&
            string.Equals(_lastLoadKey, key, StringComparison.Ordinal) &&
            Math.Abs(targetWidth - _lastTargetWidth) < 8 &&
            Math.Abs(targetHeight - _lastTargetHeight) < 8)
        {
            return;
        }

        if (resetView)
        {
            _zoom = 1;
            _offsetX = 0;
            _offsetY = 0;
        }

        _lastLoadKey = key;
        _lastTargetWidth = targetWidth;
        _lastTargetHeight = targetHeight;
        _lastSize = Bounds.Size;

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

    private (int Width, int Height) ComputeTargetSize(double scaling, int zoomFactor)
    {
        double viewportWidth = Bounds.Width * scaling * zoomFactor;
        double viewportHeight = Bounds.Height * scaling * zoomFactor;
        return FitMode switch
        {
            FitMode.FitWidth => ((int)Math.Round(viewportWidth), 0),
            FitMode.FitHeight => (0, (int)Math.Round(viewportHeight)),
            FitMode.Original => (0, 0),
            _ => ((int)Math.Round(viewportWidth), (int)Math.Round(viewportHeight)),
        };
    }

    private async Task LoadAsync(IImageRequest request, int width, int height, int generation, CancellationToken token)
    {
        try
        {
            DecodedImage? image = await request.LoadAsync(width, height, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                image?.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || generation != _generation)
                {
                    image?.Dispose();
                    return;
                }

                _loading = false;
                if (image is null)
                {
                    _failed = true;
                    RetireCurrent();
                }
                else
                {
                    _failed = false;
                    RetireCurrent();
                    _bitmap = image.Bitmap;
                    StartAnimation(image, generation, token);
                }

                InvalidateVisual();
                TryPreload();
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _generation) return;
                _loading = false;
                _failed = true;
                InvalidateVisual();
            });
        }
    }

    /// <summary>
    /// Starts frame playback when the freshly decoded page is animated. The
    /// loop is bounded by the load generation: any page/fit change cancels it
    /// and the control falls back to a static frame.
    /// </summary>
    private void StartAnimation(DecodedImage image, int generation, CancellationToken token)
    {
        if (!image.IsAnimated || image.FrameCount < 2) return;
        _animated = image;
        _animationTask = AnimationPlayer.PlayAsync(image, async next =>
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
                    // The render thread may still reference the previous frame;
                    // pooled frame buffers are recycled instead of collected.
                    Dispatcher.UIThread.Post(() => AnimationFramePool.Release(old), DispatcherPriority.Background);
                }

                InvalidateVisual();
                return true;
            });
        }, token);
        _ = _animationTask.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) AnimationStopReason = t.Result;
            else if (t.IsFaulted) AnimationStopReason = "task-faulted:" + t.Exception?.InnerException?.GetType().Name;
        }, TaskScheduler.Default);
    }

    private void TryPreload()
    {
        IImageRequest? preload = PreloadRequest;
        IImageRequest? current = Request;
        if (!_attached || preload is null || current is null) return;
        if (string.Equals(preload.Key, current.Key, StringComparison.Ordinal)) return;
        if (_preloaded is not null && string.Equals(_preloadedKey, preload.Key, StringComparison.Ordinal)) return;

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        (int width, int height) = ComputeTargetSize(scaling, 1);
        int generation = ++_preloadGeneration;
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = new CancellationTokenSource();
        _ = PreloadAsync(preload, width, height, generation, _preloadCancellation.Token);
    }

    private async Task PreloadAsync(IImageRequest request, int width, int height, int generation, CancellationToken token)
    {
        try
        {
            DecodedImage? image = await request.LoadAsync(width, height, token).ConfigureAwait(false);
            if (image is null) return;

            if (token.IsCancellationRequested || generation != _preloadGeneration)
            {
                image.Dispose();
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _preloadGeneration || !_attached)
                {
                    image.Dispose();
                    return;
                }

                _preloaded?.Dispose();
                _preloaded = image;
                _preloadedKey = request.Key;

                if (string.Equals(Request?.Key, request.Key, StringComparison.Ordinal) && _bitmap is null)
                {
                    _bitmap = _preloaded.Bitmap;
                    DecodedImage adopted = _preloaded;
                    _preloaded = null;
                    _preloadedKey = null;
                    _failed = false;
                    _loading = false;
                    StartAnimation(adopted, _generation, token);
                    InvalidateVisual();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Preload failures are silent; the page request will retry on demand.
        }
    }

    private void CancelLoads()
    {
        _generation++;
        _preloadGeneration++;
        _animated = null;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = null;
        _loading = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(PlaceholderBrush, bounds);

        Bitmap? bitmap = _bitmap;
        if (bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            if (_failed)
            {
                DrawStatus(context, bounds, "此页无法加载");
            }
            else if (_loading)
            {
                DrawStatus(context, bounds, "载入中…");
            }

            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double fitScale = GetFitScale(bitmap.PixelSize, scaling);
        double scale = fitScale * _zoom;
        double width = bitmap.PixelSize.Width * scale;
        double height = bitmap.PixelSize.Height * scale;
        double left = (Bounds.Width - width) / 2 + _offsetX;
        double top = (Bounds.Height - height) / 2 + _offsetY;

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            context.DrawImage(bitmap, new Rect(bitmap.PixelSize.ToSize(1)), new Rect(left, top, width, height));
        }
    }

    private static void DrawStatus(DrawingContext context, Rect bounds, string text)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            12,
            MutedTextBrush);
        context.DrawText(formatted, new Point((bounds.Width - formatted.Width) / 2, (bounds.Height - formatted.Height) / 2));
    }

    public void ResetView()
    {
        _zoom = 1.0;
        _offsetX = 0;
        _offsetY = 0;
        InvalidateVisual();
    }

    public void SetZoom(double zoom)
    {
        if (_bitmap is null) return;
        ApplyZoom(Math.Clamp(zoom, MinZoom, MaxZoom), new Point(Bounds.Width / 2, Bounds.Height / 2));
        InvalidateVisual();
    }

    public void PanBy(double dx, double dy)
    {
        _offsetX += dx;
        _offsetY += dy;
        ClampOffsets();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (HandleWheel(e.Delta.Y, e.KeyModifiers, e.GetPosition(this)))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Test hook: drives the wheel pipeline without a real pointer, so the
    /// "broken page must still turn" behaviour can be asserted in --ui-smoke.
    /// </summary>
    internal bool SimulateWheel(double deltaY, KeyModifiers modifiers = KeyModifiers.None) =>
        HandleWheel(deltaY, modifiers, new Point(Bounds.Width / 2, Bounds.Height / 2));

    private bool HandleWheel(double wheelDelta, KeyModifiers modifiers, Point pointer)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return false;

        bool control = (modifiers & KeyModifiers.Control) != 0;
        Bitmap? bitmap = _bitmap;

        if (control)
        {
            // Zooming is meaningless without pixels; do not consume the event.
            if (bitmap is null) return false;

            double factor = Math.Pow(1.18, wheelDelta);
            ApplyZoom(Math.Clamp(_zoom * factor, MinZoom, MaxZoom), pointer);
            InvalidateVisual();
            ScheduleZoomReload();
            return true;
        }

        if (bitmap is not null)
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            double scale = GetFitScale(bitmap.PixelSize, scaling) * _zoom;
            bool overflowY = bitmap.PixelSize.Height * scale > Bounds.Height + 0.5;
            bool overflowX = bitmap.PixelSize.Width * scale > Bounds.Width + 0.5;
            bool horizontal = (modifiers & KeyModifiers.Shift) != 0;
            double panStep = 60 * Math.Max(1, Math.Abs(wheelDelta));

            if (horizontal && overflowX)
            {
                if (TryPan(-Math.Sign(wheelDelta) * panStep, 0)) return true;
            }
            else if (overflowY)
            {
                if (TryPan(0, -Math.Sign(wheelDelta) * panStep)) return true;
            }
        }

        // Page turning must not depend on the bitmap: a corrupt page still has
        // to be navigable with the wheel (and with clicks, see below).
        if (Math.Abs(wheelDelta) < 0.01) return false;
        RaisePageTurn(wheelDelta < 0 ? 1 : -1);
        return true;
    }

    private bool TryPan(double dx, double dy)
    {
        double beforeX = _offsetX;
        double beforeY = _offsetY;
        _offsetX += dx;
        _offsetY += dy;
        ClampOffsets();
        if (Math.Abs(_offsetX - beforeX) < 0.01 && Math.Abs(_offsetY - beforeY) < 0.01)
        {
            return false;
        }

        InvalidateVisual();
        return true;
    }

    private void ScheduleZoomReload()
    {
        int desiredFactor = Math.Clamp((int)Math.Ceiling(_zoom), 1, 8);
        if (desiredFactor <= _decodedZoomFactor) return;

        _zoomReloadTimer ??= CreateZoomReloadTimer();
        _zoomReloadTimer.Stop();
        _zoomReloadTimer.Start();
    }

    private DispatcherTimer CreateZoomReloadTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            int desired = Math.Clamp((int)Math.Ceiling(_zoom), 1, 8);
            if (desired <= _decodedZoomFactor) return;
            _decodedZoomFactor = desired;
            BeginLoad(resetView: false);
        };
        return timer;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Focus and page-turn tracking stay available on a failed page; only
        // zoom/pan gestures need a decoded bitmap.
        Focus();
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (_bitmap is not null && e.ClickCount == 2)
        {
            ToggleHundredPercent(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        _pressPoint = point.Position;
        _lastPoint = point.Position;
        _dragged = false;
        if (ContentOverflows())
        {
            e.Pointer.Capture(this);
            _pointerCaptured = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pointerCaptured) return;
        Point current = e.GetPosition(this);
        double dx = current.X - _lastPoint.X;
        double dy = current.Y - _lastPoint.Y;
        if (Math.Abs(current.X - _pressPoint.X) > DragThreshold ||
            Math.Abs(current.Y - _pressPoint.Y) > DragThreshold)
        {
            _dragged = true;
        }

        if (_dragged)
        {
            PanBy(dx, dy);
        }

        _lastPoint = current;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_pointerCaptured)
        {
            e.Pointer.Capture(null);
            _pointerCaptured = false;
        }

        if (!_dragged && e.InitialPressMouseButton == MouseButton.Left)
        {
            Point point = e.GetPosition(this);
            double x = point.X / Math.Max(1, Bounds.Width);
            if (x < 1.0 / 3.0)
            {
                RaisePageTurn(RightToLeft ? 1 : -1);
                e.Handled = true;
            }
            else if (x > 2.0 / 3.0)
            {
                RaisePageTurn(RightToLeft ? -1 : 1);
                e.Handled = true;
            }
        }

        _dragged = false;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pointerCaptured = false;
        _dragged = false;
    }

    private bool ContentOverflows()
    {
        if (_bitmap is null) return false;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double scale = GetFitScale(_bitmap.PixelSize, scaling) * _zoom;
        return _bitmap.PixelSize.Width * scale > Bounds.Width + 0.5 ||
               _bitmap.PixelSize.Height * scale > Bounds.Height + 0.5;
    }

    private void ToggleHundredPercent(Point pointer)
    {
        if (_bitmap is null) return;
        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double fit = GetFitScale(_bitmap.PixelSize, scaling);
        double hundredPercentZoom = fit <= 0 ? MaxZoom : (1.0 / scaling) / fit;
        double target = _zoom > MinZoom + 0.001 ? MinZoom : Math.Clamp(hundredPercentZoom, MinZoom, MaxZoom);
        ApplyZoom(target, pointer);
        InvalidateVisual();
        ScheduleZoomReload();
    }

    private void ApplyZoom(double newZoom, Point anchor)
    {
        if (_bitmap is null) return;
        double oldZoom = _zoom;
        if (Math.Abs(oldZoom - newZoom) < 0.0001) return;

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double fit = GetFitScale(_bitmap.PixelSize, scaling);
        if (fit <= 0) return;

        double oldScale = fit * oldZoom;
        double newScale = fit * newZoom;
        double centerX = Bounds.Width / 2;
        double centerY = Bounds.Height / 2;
        double imageX = (anchor.X - centerX - _offsetX) / oldScale;
        double imageY = (anchor.Y - centerY - _offsetY) / oldScale;
        _offsetX = anchor.X - centerX - imageX * newScale;
        _offsetY = anchor.Y - centerY - imageY * newScale;
        _zoom = newZoom;
        ClampOffsets();
    }

    private double GetFitScale(PixelSize image, double scaling)
    {
        if (image.Width <= 0 || image.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return 1;
        return FitMode switch
        {
            FitMode.FitWidth => Bounds.Width / image.Width,
            FitMode.FitHeight => Bounds.Height / image.Height,
            FitMode.Original => 1.0 / Math.Max(0.1, scaling),
            _ => Math.Min(Bounds.Width / image.Width, Bounds.Height / image.Height),
        };
    }

    private void ClampOffsets()
    {
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            _offsetX = 0;
            _offsetY = 0;
            return;
        }

        double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        double scale = GetFitScale(_bitmap.PixelSize, scaling) * _zoom;
        double maxX = Math.Max(0, (_bitmap.PixelSize.Width * scale - Bounds.Width) / 2);
        double maxY = Math.Max(0, (_bitmap.PixelSize.Height * scale - Bounds.Height) / 2);
        _offsetX = Math.Clamp(_offsetX, -maxX, maxX);
        _offsetY = Math.Clamp(_offsetY, -maxY, maxY);
    }

    private void RaisePageTurn(int delta)
    {
        if (delta == 0) return;
        PageTurnRequested?.Invoke(this, new PageTurnEventArgs(delta));
    }
}
