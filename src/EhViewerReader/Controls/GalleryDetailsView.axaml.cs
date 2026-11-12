using Avalonia;
using Avalonia.Controls;
using EhViewerReader.ViewModels;

namespace EhViewerReader.Controls;

public partial class GalleryDetailsView : UserControl
{
    public static readonly StyledProperty<GalleryDetailsViewModel?> DetailsProperty = AvaloniaProperty.Register<GalleryDetailsView, GalleryDetailsViewModel?>(nameof(Details));
    public static readonly StyledProperty<Control?> ActionsProperty = AvaloniaProperty.Register<GalleryDetailsView, Control?>(nameof(Actions));
    public static readonly StyledProperty<Control?> SideContentProperty = AvaloniaProperty.Register<GalleryDetailsView, Control?>(nameof(SideContent));
    public static readonly StyledProperty<Control?> ExtraContentProperty = AvaloniaProperty.Register<GalleryDetailsView, Control?>(nameof(ExtraContent));
    public static readonly StyledProperty<Control?> FooterProperty = AvaloniaProperty.Register<GalleryDetailsView, Control?>(nameof(Footer));
    private bool? _narrow;
    public GalleryDetailsView()
    {
        InitializeComponent();
        DetailsGrid.SizeChanged += (_, e) => AdaptDetails(e.NewSize.Width < 740);
    }

    private void AdaptDetails(bool narrow)
    {
        if (_narrow == narrow) return;
        _narrow = narrow;
        CoverFrame.Height = narrow ? 160 : 374;
        CoverPanel.ColumnDefinitions = new ColumnDefinitions(narrow ? "120,*" : "*");
        CoverPanel.RowDefinitions = new RowDefinitions(narrow ? "Auto" : "Auto,Auto");
        CoverPanel.ColumnSpacing = narrow ? 16 : 0;
        Grid.SetRow(SidePanel, narrow ? 0 : 1);
        Grid.SetColumn(SidePanel, narrow ? 1 : 0);
        DetailsColumn.Children.Remove(TagSection);
        DetailsColumn.Children.Insert(narrow ? 0 : 1, TagSection);
    }
    public GalleryDetailsViewModel? Details { get => GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }
    public Control? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public Control? SideContent { get => GetValue(SideContentProperty); set => SetValue(SideContentProperty, value); }
    public Control? ExtraContent { get => GetValue(ExtraContentProperty); set => SetValue(ExtraContentProperty, value); }
    public Control? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }
}
