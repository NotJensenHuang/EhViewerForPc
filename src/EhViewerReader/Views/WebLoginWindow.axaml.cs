using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class WebLoginWindow : Window
{
    private readonly WebLoginViewModel? _viewModel;
    private bool _closed;

    public WebLoginWindow()
    {
        InitializeComponent();
        Icon = AppIcons.Window;
    }

    public WebLoginWindow(WebLoginViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Completed += OnCompleted;
        Opened += (_, _) => viewModel.Start();
        Closed += (_, _) =>
        {
            viewModel.Completed -= OnCompleted;
            viewModel.Dispose();
        };
    }

    private void OnCompleted(object? sender, Core.Online.EhLoginResult result)
    {
        if (_closed) return;
        _closed = true;

        // Leave the result visible for a moment so the user sees why the window
        // closed, then get out of the way.
        _ = CloseSoonAsync();
    }

    private async Task CloseSoonAsync()
    {
        await Task.Delay(600);
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || Clipboard is null) return;
        await Clipboard.SetTextAsync(_viewModel.LoginUrl);
        _viewModel.SetStatus("登录地址已复制");
    }
}
