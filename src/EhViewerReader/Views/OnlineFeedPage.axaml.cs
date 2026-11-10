using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class OnlineFeedPage : UserControl
{
    public OnlineFeedPage()
    {
        InitializeComponent();
    }

    /// <summary>Ctrl+F lands here: focus the merged search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: FeedOption option })
        {
            viewModel.SelectedTab = option;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is OnlineFeedViewModel viewModel)
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFavoriteSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is OnlineFeedViewModel viewModel)
        {
            viewModel.SearchFavoritesCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: string entry })
        {
            viewModel.UseHistoryCommand.Execute(entry);
        }
    }

    private void OnSavedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: string name })
        {
            viewModel.UseSavedSearchCommand.Execute(name);
        }
    }

    private void OnSavedRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineFeedViewModel viewModel) return;
        if (sender is Button { Tag: string name })
        {
            viewModel.RemoveSavedSearchCommand.Execute(name);
        }
    }
}
