using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EhViewerReader.Core.Models;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly MainViewModel _mainViewModel;
    private readonly AppTheme _originalTheme;
    private bool _saved;

    public SettingsWindow(SettingsViewModel viewModel, MainViewModel mainViewModel)
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.RenderOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
        _viewModel = viewModel;
        _mainViewModel = mainViewModel;
        _originalTheme = mainViewModel.Services.Settings.Theme;
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_saved)
        {
            App.ApplyTheme(_originalTheme);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        _saved = true;
        bool sourcesChanged = _viewModel.SourcesChanged;
        _viewModel.Apply();
        Close();

        if (sourcesChanged)
        {
            if (_viewModel.HasSources)
            {
                await _mainViewModel.ReloadSourcesAsync();
            }
            else
            {
                _mainViewModel.ClearLibraryView();
            }
        }
        else
        {
            _mainViewModel.ApplyFilter();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        App.ApplyTheme(_originalTheme);
        _saved = true;
        Close();
    }

    private void OnResetCache(object? sender, RoutedEventArgs e)
    {
        _mainViewModel.ClearCaches();
        _mainViewModel.ReportError("扫描缓存已清除，刷新后重新扫描（资料库未被修改）");
    }

    private void OnOpenDataDirectory(object? sender, RoutedEventArgs e)
    {
        _viewModel.OpenDataDirectory();
    }

    private async void OnAddSource(object? sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "添加 EhViewer 下载目录",
                    AllowMultiple = false,
                });
            if (folders.Count == 0) return;

            string? path = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            string? error = _mainViewModel.ValidateSource(path);
            if (error is not null)
            {
                _mainViewModel.ReportError("添加数据源失败：" + error);
                return;
            }

            _viewModel.AddSource(path);
        }
        catch (Exception ex)
        {
            _mainViewModel.ReportError("选择目录失败：" + ex.Message);
        }
    }

    private void OnRemoveSource(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SourceRowViewModel row })
        {
            _viewModel.RemoveSource(row);
        }
    }

    private void OnOpenSource(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SourceRowViewModel row })
        {
            _viewModel.OpenSource(row);
        }
    }
}
