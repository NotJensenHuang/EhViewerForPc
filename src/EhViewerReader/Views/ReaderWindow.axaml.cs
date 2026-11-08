using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EhViewerReader.Controls;
using EhViewerReader.Core.Models;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class ReaderWindow : Window
{
    private readonly ReaderViewModel _viewModel;
    private readonly DispatcherTimer _resizeTimer;
    private int _scrollRequestSequence;

    public ReaderWindow(ReaderViewModel viewModel)
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.RenderOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        _viewModel = viewModel;
        DataContext = viewModel;

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _resizeTimer.Tick += OnResizeSettled;

        viewModel.ScrollToRequested += OnScrollToRequested;
        Loaded += OnLoaded;
        Closing += OnClosing;
        SizeChanged += OnWindowSizeChanged;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        SingleView.Focus();
    }

    /// <summary>Stable identifier of the gallery being read; used to avoid two
    /// reader windows writing the same progress record.</summary>
    public string? GalleryId { get; init; }

    /// <summary>Test hook: drives the wheel pipeline without a real pointer.</summary>
    internal bool SimulateWheelForTest(double deltaY) => SingleView.SimulateWheel(deltaY);

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            UpdateContinuousWidth();
            await _viewModel.InitializeAsync();
            SingleView.Focus();
        }
        catch (Exception ex)
        {
            // A failure here would otherwise surface as an unhandled async-void
            // exception and tear down the process.
            _viewModel.Services.Log.Error("Reader initialisation failed: " + _viewModel.Title, ex);
            Title = "阅读器 · 打开失败：" + ex.Message;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _viewModel.ScrollToRequested -= OnScrollToRequested;
        _viewModel.Dispose();
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!_viewModel.IsContinuousMode) return;
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeSettled(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        UpdateContinuousWidth();
    }

    private void UpdateContinuousWidth()
    {
        double viewport = ContinuousScroll.Viewport.Width;
        if (viewport <= 0) viewport = Bounds.Width;
        _viewModel.SetContinuousWidth(viewport);
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

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_viewModel.IsContinuousMode) return;
        _viewModel.UpdateScrollOffset(ContinuousScroll.Offset.Y, ContinuousScroll.Viewport.Height);
    }

    private void OnPageTurnRequested(object? sender, PageTurnEventArgs e)
    {
        _viewModel.Step(e.Delta);
    }

    private void OnBack(object? sender, RoutedEventArgs e) => Close();

    private async void OnSingleMode(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SetModeAsync(ReadingMode.Single);
        SingleView.Focus();
    }

    private async void OnContinuousMode(object? sender, RoutedEventArgs e)
    {
        await _viewModel.SetModeAsync(ReadingMode.Continuous);
    }

    private void OnToggleDirection(object? sender, RoutedEventArgs e) => _viewModel.ToggleDirection();

    private void OnToggleBackground(object? sender, RoutedEventArgs e) => _viewModel.ToggleBackground();

    private void OnToggleFullscreen(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnShowHelp(object? sender, RoutedEventArgs e) => ShowHelp();

    private void ToggleFullscreen()
    {
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool rtl = _viewModel.RightToLeft;
        switch (e.Key)
        {
            case Key.Escape:
                if (WindowState == WindowState.FullScreen)
                {
                    WindowState = WindowState.Normal;
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
                _viewModel.Step(rtl ? 1 : -1);
                e.Handled = true;
                return;

            case Key.Right:
                _viewModel.Step(rtl ? -1 : 1);
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
        ReadingMode target = _viewModel.IsSingleMode ? ReadingMode.Continuous : ReadingMode.Single;
        await _viewModel.SetModeAsync(target);
        if (target == ReadingMode.Single) SingleView.Focus();
    }

    private void ShowHelp()
    {
        const string text = """
            阅读器快捷键

            ← / →        翻页（随阅读方向自动反转）
            PageUp / PageDown / 空格   翻页
            Home / End   首页 / 末页
            + / - / 0    放大 / 缩小 / 适应
            Ctrl + 滚轮  以指针为中心缩放
            双击         适应页面 ↔ 100%
            滚轮         内容溢出时平移，到边界后翻页
            单击左/右 1/3   翻页
            F11          全屏
            Esc          退出全屏；再按关闭阅读器
            M            切换单页 / 连续
            D            切换阅读方向
            B            切换背景色
            ?            显示本帮助
            """;

        var help = new Window
        {
            Title = "快捷键",
            Width = 460,
            Height = 460,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#0B0D10")),
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F2F4F8")),
            Content = new ScrollViewer
            {
                Padding = new Thickness(20),
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    LineHeight = 22,
                },
            },
        };

        help.ShowDialog(this);
    }
}
