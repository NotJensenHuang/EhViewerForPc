using Avalonia.Controls;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Views;

public partial class OnlineGalleryPage : DocumentPage
{
    public OnlineGalleryPage()
    {
        InitializeComponent();
        Avalonia.Media.TextOptions.SetTextRenderingMode(this, Avalonia.Media.TextRenderingMode.Antialias);
    }

    public OnlineGalleryPage(OnlineGalleryViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }
    public override void Dispose() { if (DataContext is OnlineGalleryViewModel vm) vm.Dispose(); }
}
