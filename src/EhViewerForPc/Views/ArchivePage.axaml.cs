using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class ArchivePage : DocumentPage
{
    public ArchivePage()
    {
        InitializeComponent();
    }

    public ArchivePage(ArchiveViewModel viewModel, bool initializeOnLoad = true) : this()
    {
        DataContext = viewModel;
        if (initializeOnLoad) Loaded += async (_, _) => await viewModel.InitializeAsync();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
