using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EhViewerForPc.Controls;
using EhViewerForPc.Services;
using EhViewerForPc.Core.Models;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class ReaderPage : DocumentPage
{
    private readonly ReaderViewModel _viewModel;
    private readonly DispatcherTimer _resizeTimer;
    private int _scrollRequestSequence;
    private int _horizontalScrollSequence;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;
    private bool _initialized;
    private bool _disposed;

    public ReaderPage(ReaderViewModel viewModel)
    {
        InitializeComponent();
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        _viewModel = viewModel;
        DataContext = viewModel;
        Focusable = true;

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeTimer.Tick += OnResizeSettled;

        viewModel.ScrollToRequested += OnScrollToRequested;
        viewModel.HorizontalScrollToRequested += OnHorizontalScrollToRequested;
        Loaded += OnLoaded;

        SizeChanged += OnWindowSizeChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

    }

    /// <summary>Stable identifier of the gallery being read; used to avoid two
    /// reader windows writing the same progress record.</summary>
    public string? GalleryId { get; init; }
    public ReaderViewModel ViewModel => _viewModel;
    public bool IsImmersive { get; private set; }
    public event EventHandler<bool>? ImmersiveChanged;

    private void OnExitImmersive(object? sender, RoutedEventArgs e) => SetImmersive(false);

    internal void ToggleImmersive() => SetImmersive(!IsImmersive);

    private void SetImmersive(bool value)
    {
        IsImmersive = value;
        ImmersiveExit.IsVisible = value;
        ImmersiveChanged?.Invoke(this, value);
        FocusReader();
    }

    /// <summary>Test hook: drives the wheel pipeline without a real pointer.</summary>
    internal bool SimulateWheelForTest(double deltaY) => SingleView.SimulateWheel(deltaY);

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            UpdateContinuousWidth();
            UpdateContinuousHeight();
            FocusReader();
            if (!_initialized) { _initialized = true; await _viewModel.InitializeAsync(); }
            if (!_disposed) FocusReader();
        }
        catch (Exception ex)
        {
            // A failure here would otherwise surface as an unhandled async-void
            // exception and tear down the process.
            _viewModel.Services.Log.Error("Reader initialisation failed: " + _viewModel.Title, ex);
            Title = "阅读器 · 打开失败：" + ex.Message;
        }
    }

    private void FocusReader()
    {
        if (_viewModel.IsSingleMode) SingleView.Focus();
        else Focus();
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _viewModel.ScrollToRequested -= OnScrollToRequested;
        _viewModel.HorizontalScrollToRequested -= OnHorizontalScrollToRequested;
        _resizeTimer.Stop();
        _viewModel.Dispose();
        AnimationFramePool.Clear();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_viewModel.IsSingleMode) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        UpdateContinuousWidth();
        UpdateContinuousHeight();
    }

    private void UpdateContinuousWidth()
    {
        if (_viewModel.IsHorizontalMode) return;
        double viewport = ContinuousScroll.Viewport.Width;
        if (viewport <= 0) viewport = Bounds.Width;
        _viewModel.SetContinuousWidth(viewport);
    }

    private void UpdateContinuousHeight()
    {
        if (!_viewModel.IsHorizontalMode) return;
        double viewport = HorizontalScroll.Viewport.Height;
        if (viewport <= 0) viewport = Bounds.Height;
        _viewModel.SetContinuousHeight(viewport);
    }

    private void OnScrollToRequested(object? sender, double offset)
    {
        int sequence = ++_scrollRequestSequence;
        Dispatcher.UIThread.Post(() =>
        {
            if (sequence != _scrollRequestSequence) return;
            ContinuousScroll.Offset = new Vector(0, Math.Max(0, offset));
            _viewModel.UpdateScrollOffset(ContinuousScroll.Offset.Y, ContinuousScroll.Viewport.Height);
        }, DispatcherPriority.Loaded);
    }

    private void OnHorizontalScrollToRequested(object? sender, double offset)
    {
        int sequence = ++_horizontalScrollSequence;
        Dispatcher.UIThread.Post(() =>
        {
            if (sequence != _horizontalScrollSequence) return;
            ApplyHorizontalOffset(offset, sequence);
        }, DispatcherPriority.Loaded);
    }

    private void ApplyHorizontalOffset(double offset, int sequence)
    {
        if (!_viewModel.IsHorizontalMode) return;
        HorizontalScroll.Offset = new Vector(Math.Max(0, offset), 0);
        _viewModel.UpdateScrollOffsetX(HorizontalScroll.Offset.X, HorizontalScroll.Viewport.Width);

        // A mode switch or a slider jump can outrun the layout: the extent is
        // still settling and Offset gets clamped to a smaller value. Retry at
        // Background once the layout has caught up.
        Dispatcher.UIThread.Post(() =>
        {
            if (sequence != _horizontalScrollSequence) return;
            if (Math.Abs(HorizontalScroll.Offset.X - offset) > 1)
            {
                HorizontalScroll.Offset = new Vector(Math.Max(0, offset), 0);
            }
        }, DispatcherPriority.Background);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel.IsHorizontalMode)
        {
            _viewModel.UpdateScrollOffsetX(HorizontalScroll.Offset.X, HorizontalScroll.Viewport.Width);
        }
        else if (_viewModel.IsContinuousMode)
        {
            _viewModel.UpdateScrollOffset(ContinuousScroll.Offset.Y, ContinuousScroll.Viewport.Height);
        }
    }

    private void OnHorizontalWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!_viewModel.IsHorizontalMode) return;
        double delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01) return;
        e.Handled = true;
        if ((e.KeyModifiers & KeyModifiers.Control) != 0) return; // no zoom in horizontal mode

        // Small per-notch step keeps horizontal panning pixel-smooth instead
        // of jumping half a viewport per wheel tick.
        double step = Math.Clamp(HorizontalScroll.Viewport.Width * 0.12, 48, 240) * delta;
        HorizontalScroll.Offset = HorizontalScroll.Offset + new Vector(-step, 0);
    }

    private void OnPageTurnRequested(object? sender, PageTurnEventArgs e)
    {
        _viewModel.Step(e.Delta);
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Close();

    // Mode commands live in the shell header; the page owns the async switch so
    // the continuous viewports can be re-measured right after the mode change.
    internal Task SetSingleModeAsync() => SwitchModeAsync(ReadingMode.Single);

    internal Task ToggleContinuousModeAsync() => SwitchModeAsync(_viewModel.IsContinuousMode
        ? ReadingMode.Single
        : ReadingMode.Continuous);

    internal Task ToggleHorizontalModeAsync() => SwitchModeAsync(_viewModel.IsHorizontalMode
        ? ReadingMode.Single
        : ReadingMode.Double);

    private async Task SwitchModeAsync(ReadingMode mode)
    {
        await _viewModel.SetModeAsync(mode);
        if (_viewModel.IsContinuousMode) UpdateContinuousWidth();
        else if (_viewModel.IsHorizontalMode) UpdateContinuousHeight();
        FocusReader();
    }

    internal void ToggleDirection() => _viewModel.ToggleDirection();
    internal void SetDirection(bool rightToLeft) => _viewModel.SetDirection(rightToLeft);

    internal void ToggleBackground() => _viewModel.ToggleBackground();


    internal void ToggleFullscreen() => SetFullscreen(WindowState != WindowState.FullScreen);

    internal void ShowHelpFlyout() => ShowHelp();

    private void SetFullscreen(bool fullScreen)
    {
        if (TopLevel.GetTopLevel(this) is not Window window) return;
        if (fullScreen)
        {
            if (window.WindowState == WindowState.FullScreen) return;
            // Remember Normal vs Maximized so leaving full screen restores the
            // window the user actually had before.
            _stateBeforeFullscreen = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
        else
        {
            window.WindowState = _stateBeforeFullscreen == WindowState.Maximized
                ? WindowState.Maximized
                : WindowState.Normal;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right) return;
        switch (e.Key)
        {
            case Key.Escape:
                if (IsImmersive) { SetImmersive(false); e.Handled = true; return; }
                if (WindowState == WindowState.FullScreen)
                {
                    SetFullscreen(false);
                }
                else
                {
                    Close();
                }

                e.Handled = true;
                return;

            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                return;

            case Key.Left:
                _viewModel.Step(-1);
                e.Handled = true;
                return;

            case Key.Right:
                _viewModel.Step(1);
                e.Handled = true;
                return;

            case Key.PageDown:
            case Key.Space:
                _viewModel.Step(1);
                e.Handled = true;
                return;

            case Key.PageUp:
            case Key.Back:
                _viewModel.Step(-1);
                e.Handled = true;
                return;

            case Key.Home:
                _viewModel.GoToIndex(0, scrollContinuous: true);
                e.Handled = true;
                return;

            case Key.End:
                _viewModel.GoToIndex(_viewModel.PageCount - 1, scrollContinuous: true);
                e.Handled = true;
                return;

            case Key.M:
                _ = ToggleModeAsync();
                e.Handled = true;
                return;

            case Key.D:
                _viewModel.ToggleDirection();
                e.Handled = true;
                return;

            case Key.B:
                _viewModel.ToggleBackground();
                e.Handled = true;
                return;

            case Key.OemQuestion:
                ShowHelp();
                e.Handled = true;
                return;

            case Key.OemPlus:
            case Key.Add:
                SingleView.SetZoom(SingleView.CurrentZoom * 1.25);
                e.Handled = true;
                return;

            case Key.OemMinus:
            case Key.Subtract:
                SingleView.SetZoom(SingleView.CurrentZoom / 1.25);
                e.Handled = true;
                return;

            case Key.D0:
            case Key.NumPad0:
                SingleView.ResetView();
                e.Handled = true;
                return;
        }
    }

    private async Task ToggleModeAsync()
    {
        ReadingMode target = _viewModel.IsSingleMode
            ? ReadingMode.Continuous
            : _viewModel.IsContinuousMode
                ? ReadingMode.Double
                : ReadingMode.Single;
        await _viewModel.SetModeAsync(target);
        if (target == ReadingMode.Continuous) UpdateContinuousWidth();
        else UpdateContinuousHeight();
        FocusReader();
    }

    private void ShowHelp()
    {
        const string text = """
            阅读器快捷键

            ← / →        上一页 / 下一页
            PageUp / PageDown / 空格   翻页
            Home / End   首页 / 末页
            + / - / 0    放大 / 缩小 / 适应
            Ctrl + 滚轮  以指针为中心缩放
            双击         适应页面 ↔ 100%
            滚轮         单页：内容溢出时平移，到边界后翻页
                         横向模式：横向滚动
            单击左/右 1/3   翻页（单页模式）
            F11          全屏
            Esc          退出全屏；再按关闭阅读器
            M            切换单页 / 纵向连续 / 横向连续
            D            切换横向连续的页面排列
            B            切换背景色
            ?            显示本帮助
            """;

        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 460, Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(16) } } };
        flyout.ShowAt(this);
    }
}
