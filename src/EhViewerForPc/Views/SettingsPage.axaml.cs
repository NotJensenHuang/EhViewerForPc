using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class SettingsPage : UserControl
{
    private SettingsViewModel? _hooked;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Raised after "保存全部设置"; the shell reloads sources and theme.</summary>
    public event Func<bool, Task>? SettingsApplied;

    /// <summary>Raised by the "网页登录" button; the shell owns the login window.</summary>
    public event EventHandler? WebLoginRequested;
    public event EventHandler? MangaTranslationSettingsRequested;

    /// <summary>Raised by "开始扫描"; the shell runs the full library rescan.</summary>
    public event EventHandler? RescanRequested;

    /// <summary>Raised by "取消"; the shell cancels the running scan.</summary>
    public event EventHandler? CancelRescanRequested;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || ReferenceEquals(viewModel, _hooked)) return;
        Unhook();
        _hooked = viewModel;
        viewModel.RescanRequested += OnViewModelRescan;
        viewModel.CancelRescanRequested += OnViewModelCancelRescan;
        viewModel.RefreshCacheUsage();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => Unhook();

    private void Unhook()
    {
        if (_hooked is null) return;
        _hooked.RescanRequested -= OnViewModelRescan;
        _hooked.CancelRescanRequested -= OnViewModelCancelRescan;
        _hooked = null;
    }

    private void OnViewModelRescan(object? sender, EventArgs e) =>
        RescanRequested?.Invoke(this, EventArgs.Empty);

    private void OnViewModelCancelRescan(object? sender, EventArgs e) =>
        CancelRescanRequested?.Invoke(this, EventArgs.Empty);

    private void OnWebLogin(object? sender, RoutedEventArgs e) =>
        WebLoginRequested?.Invoke(this, EventArgs.Empty);

    private void OnMangaTranslationSettings(object? sender, RoutedEventArgs e) =>
        MangaTranslationSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnOpenDataDirectory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.OpenDataDirectory();
    }

    private void OnClearOnlineCache(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.ClearOnlineCache();
    }

    private void OnClearCache(object? sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel) viewModel.ClearCaches();
    }

    private async void OnApplyAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        await viewModel.ApplyAsync(changed => SettingsApplied?.Invoke(changed) ?? Task.CompletedTask);
    }

    private async void OnPickDownloadRoot(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        string? path = await PickFolderAsync("选择下载根目录");
        if (path is not null) viewModel.SetDownloadRoot(path);
    }

    private async void OnAddSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        string? path = await PickFolderAsync("添加 EhViewer 下载目录");
        if (path is not null) viewModel.AddSource(path);
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        if (sender is Button { Tag: SourceRowViewModel row }) viewModel.OpenSource(row);
    }

    private void OnRemoveSource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        if (sender is Button { Tag: SourceRowViewModel row }) viewModel.RemoveSource(row);
    }

    private void OnRemoveFilter(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel) return;
        if (sender is Button { Tag: FilterRuleViewModel row }) viewModel.RemoveFilterCommand.Execute(row);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
