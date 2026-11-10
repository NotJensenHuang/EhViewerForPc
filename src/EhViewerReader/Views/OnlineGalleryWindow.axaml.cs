using Avalonia.Controls;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class OnlineGalleryWindow : Window
{
    public OnlineGalleryWindow()
    {
        InitializeComponent();
        Icon = AppIcons.Window;
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
    }

    public OnlineGalleryWindow(OnlineGalleryViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += async (_, _) => await viewModel.InitializeAsync();
    }
}
