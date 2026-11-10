using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class SearchPage : UserControl
{
    public SearchPage()
    {
        InitializeComponent();
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is SearchPageViewModel viewModel)
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnHistoryClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SearchPageViewModel viewModel) return;
        if (sender is Button { Tag: string entry })
        {
            viewModel.UseHistoryCommand.Execute(entry);
        }
    }

    private void OnSavedClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SearchPageViewModel viewModel) return;
        if (sender is Button { Tag: string name })
        {
            viewModel.UseSavedSearchCommand.Execute(name);
        }
    }

    private void OnSavedRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SearchPageViewModel viewModel) return;
        if (sender is Button { Tag: string name })
        {
            viewModel.RemoveSavedSearchCommand.Execute(name);
        }
    }
}
