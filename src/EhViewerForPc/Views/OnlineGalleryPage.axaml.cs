using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EhViewerForPc.ViewModels;
using FluentAvalonia.UI.Controls;

namespace EhViewerForPc.Views;

public partial class OnlineGalleryPage : DocumentPage
{
    private bool _initialized;
    private bool _favoriteDialogOpen;

    public OnlineGalleryPage()
    {
        InitializeComponent();
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
    }

    public OnlineGalleryPage(OnlineGalleryViewModel viewModel, bool initializeOnLoad = true) : this()
    {
        DataContext = viewModel;
        if (initializeOnLoad) Loaded += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await viewModel.InitializeAsync();
        };
    }

    /// <summary>
    /// Favourite flow: a Fluent dialog seeded with the real category, no request
    /// on open or on selection change, and no request when the dialog is closed.
    /// </summary>
    private async void OnFavoriteClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OnlineGalleryViewModel viewModel || _favoriteDialogOpen) return;
        if (!viewModel.CanOpenFavoriteDialog) return;
        if (!viewModel.IsSignedIn)
        {
            viewModel.NotifySignInRequired();
            return;
        }

        _favoriteDialogOpen = true;
        try
        {
            FavoriteDraft? draft = await viewModel.PrepareFavoriteDraftAsync();
            draft ??= new FavoriteDraft(viewModel.IsFavorite, -1, string.Empty);

            var list = new ListBox
            {
                MaxHeight = 300,
                MinWidth = 320,
                Margin = new Thickness(0, 8, 0, 0),
            };
            foreach (FeedOption option in viewModel.FavoriteCategories)
            {
                list.Items.Add(new ListBoxItem { Content = option.Label, Tag = option.Key });
            }

            int initial = draft.Slot is >= 0 and <= 9 ? draft.Slot : 0;
            list.SelectedIndex = initial;

            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = draft.IsFavorite
                    ? "选择新的分类，或点击“取消收藏”。关闭对话框不会修改服务器。"
                    : "选择一个收藏分类，点击“保存”后提交。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
            if (draft.Note.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "站点上已有的收藏备注会保留。",
                    FontSize = 12,
                    Opacity = 0.6,
                });
            }

            panel.Children.Add(list);

            var dialog = new FAContentDialog
            {
                Title = draft.IsFavorite ? "收藏设置" : "添加到收藏",
                Content = panel,
                PrimaryButtonText = "保存",
                SecondaryButtonText = draft.IsFavorite ? "取消收藏" : null,
                CloseButtonText = "取消",
                DefaultButton = FAContentDialogButton.Primary,
            };

            TopLevel? top = TopLevel.GetTopLevel(this);
            FAContentDialogResult result = top is Window window
                ? await dialog.ShowAsync(window)
                : await dialog.ShowAsync();

            if (result == FAContentDialogResult.None) return;

            if (result == FAContentDialogResult.Secondary)
            {
                await viewModel.ApplyFavoriteAsync(-1, add: false);
                return;
            }

            int slot = Math.Clamp(list.SelectedIndex, 0, 9);
            if (draft.IsFavorite && draft.Slot == slot)
            {
                // Same category: saving is a no-op, so do not post anything.
                return;
            }

            await viewModel.ApplyFavoriteAsync(slot, add: true);
        }
        catch (Exception ex)
        {
            // The favourite dialog must never take the window down.
            Services.AppServices.Current.Log.Warn("Favorite dialog failed", ex);
        }
        finally
        {
            _favoriteDialogOpen = false;
        }
    }

    public override void Dispose() { if (DataContext is OnlineGalleryViewModel vm) vm.Dispose(); }
}
