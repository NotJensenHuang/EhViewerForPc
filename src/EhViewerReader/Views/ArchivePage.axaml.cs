using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

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
