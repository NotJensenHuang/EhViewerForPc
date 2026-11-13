using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerForPc.ViewModels;

namespace EhViewerForPc.Views;

public partial class DownloadsPage : UserControl
{
    public DownloadsPage()
    {
        InitializeComponent();
    }
    private void OnBrowse(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.Shell?.Navigate("online");
    private void OnSettings(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.Shell?.Navigate("settings");
    private void OnArchive(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DownloadsViewModel vm && sender is Button { Tag: ArchiveViewModel session }) vm.ShowArchiveCommand.Execute(session);
    }
}
