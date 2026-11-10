using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class ArchiveWindow : Window
{
    public ArchiveWindow()
    {
        InitializeComponent();
        Icon = AppIcons.Window;
    }

    public ArchiveWindow(ArchiveViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
