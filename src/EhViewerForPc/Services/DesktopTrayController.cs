using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using EhViewerForPc.ViewModels;
using EhViewerForPc.Views;

namespace EhViewerForPc.Services;

/// <summary>由 App 持有的托盘 UI 生命周期；隐藏主窗不会释放应用服务或阅读文档。</summary>
internal sealed class DesktopTrayController(MainWindow window, AppServices services, Func<bool> requestShutdown) : IDisposable
{
    private TrayIcon? _icon;
    private Application? _application;
    private TrayIcons? _icons;
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
            var menu = BuildMenu();
            _icon = new TrayIcon();
            _icon.Icon = icon;
            _icon.ToolTipText = "EhViewer for PC";
            _icon.Menu = menu;
            _icon.IsVisible = true;
            _icon.Clicked += OnClicked;
            _application = Application.Current ?? throw new InvalidOperationException("应用尚未初始化。");
            _icons = TrayIcon.GetIcons(_application);
            if (_icons is null)
            {
                _icons = new TrayIcons();
                TrayIcon.SetIcons(_application, _icons);
            }
            // Avalonia 12 仅在加入 Application 的托盘集合时创建原生实现。
            _icons.Add(_icon);
            window.BackgroundCloseHandler = TryHide;
            return true;
        }
        catch (Exception error)
        {
            services.Log.Warn("Tray initialization failed", error);
            ReleaseIcon();
            return false;
        }
    }

    /// <summary>
    /// 托盘菜单直接由导航模型生成：新增页面会自动出现在菜单里，不维护第二份清单。
    /// </summary>
    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("显示窗口") { Command = new RelayCommand(Restore) });
        menu.Items.Add(new NativeMenuItemSeparator());

        if (window.Shell is { } shell)
        {
            foreach (NavItem item in shell.NavItems)
            {
                string key = item.Key;
                menu.Items.Add(new NativeMenuItem(item.Label) { Command = new RelayCommand(() => OpenPage(key)) });
            }

            menu.Items.Add(new NativeMenuItemSeparator());
        }

        menu.Items.Add(new NativeMenuItem("本地库图搜图") { Command = new RelayCommand(OpenImageSearch) });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(new NativeMenuItem("退出") { Command = new RelayCommand(Exit) });
        return menu;
    }

    /// <summary>先恢复前台再切页：隐藏窗口上跑导航动画与页面激活会被白做一次布局。</summary>
    private void OpenPage(string key)
    {
        if (_disposed) return;
        Restore();
        if (window.Shell is not { } shell) return;
        if (shell.Settings.IsSaving)
        {
            // 迁移期间导航会被拒绝，明确告知而不是静默丢弃这次点击。
            shell.Notify("请等待目录迁移完成，或在设置页取消迁移");
            return;
        }

        shell.Navigate(key);
    }

    private void OpenImageSearch()
    {
        if (_disposed) return;
        Restore();
        if (window.Shell is { } shell && shell.Settings.IsSaving)
        {
            shell.Notify("请等待目录迁移完成，或在设置页取消迁移");
            return;
        }

        window.OpenImageSearch();
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
        _application is not null && TrayIcon.GetIcons(_application)?.Contains(_icon) == true &&
        reason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined;

    private void OnClicked(object? sender, EventArgs e) => Restore();

    internal void Restore()
    {
        if (_disposed) return;
        // 与「第二实例请求激活」共用同一条恢复路径，避免两套显示窗口实现。
        WindowActivation.Restore(window, services.Settings.WindowMaximized);
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
        ReleaseIcon();
    }

    private void ReleaseIcon()
    {
        if (_icon is null) return;
        _icon.Clicked -= OnClicked;
        _icon.IsVisible = false;
        _icons?.Remove(_icon);
        _icon.Menu = null;
        _icon.Dispose();
        _icon = null;
        _icons = null;
        _application = null;
    }
}
