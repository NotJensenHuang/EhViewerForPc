using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.Views;

namespace EhViewerForPc.Services;

/// <summary>由 App 持有的托盘 UI 生命周期；隐藏主窗不会释放应用服务或阅读文档。</summary>
internal sealed class DesktopTrayController(MainWindow window, AppServices services, Func<bool> requestShutdown) : IDisposable
{
    private TrayIcon? _icon;
    private bool _exitRequested;
    private bool _disposed;
    internal TrayIcon? Icon => _icon;

    internal bool Start()
    {
        if (_disposed) return false;
        if (_icon is not null) return true;
        try
        {
            if (AppIcons.Window is not { } icon) return false;
            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem("显示窗口") { Command = new RelayCommand(Restore) });
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem("退出") { Command = new RelayCommand(Exit) });
            _icon = new TrayIcon();
            _icon.Icon = icon;
            _icon.ToolTipText = "EhViewer for PC";
            _icon.Menu = menu;
            _icon.IsVisible = true;
            _icon.Clicked += OnClicked;
            window.BackgroundCloseHandler = TryHide;
            return true;
        }
        catch (Exception error)
        {
            services.Log.Warn("Tray initialization failed", error);
            _icon?.Dispose();
            _icon = null;
            return false;
        }
    }

    internal bool TryHide(WindowClosingEventArgs e)
    {
        if (!ShouldHide(e.CloseReason)) return false;
        e.Cancel = true;
        window.Hide();
        return true;
    }

    internal bool ShouldHide(WindowCloseReason reason) => !_disposed && !_exitRequested &&
        services.Settings.CloseToTray && _icon?.IsVisible == true &&
        reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;

    private void OnClicked(object? sender, EventArgs e) => Restore();

    internal void Restore()
    {
        if (_disposed) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = services.Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        window.Activate();
    }

    internal void Exit()
    {
        if (_disposed || _exitRequested) return;
        _exitRequested = true;
        try
        {
            if (requestShutdown()) return;
        }
        catch (Exception error)
        {
            services.Log.Warn("Tray exit failed", error);
        }
        _exitRequested = false;
        Restore();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        window.BackgroundCloseHandler = null;
        if (_icon is null) return;
        _icon.Clicked -= OnClicked;
        _icon.IsVisible = false;
        _icon.Menu = null;
        _icon.Dispose();
        _icon = null;
    }
}
