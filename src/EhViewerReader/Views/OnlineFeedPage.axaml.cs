using Avalonia.Controls;
using Avalonia.Interactivity;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class OnlineFeedPage : UserControl
{
    public OnlineFeedPage()
    {
        InitializeComponent();
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: FeedOption option })
        {
            viewModel.SelectedTab = option;
        }
    }
}
