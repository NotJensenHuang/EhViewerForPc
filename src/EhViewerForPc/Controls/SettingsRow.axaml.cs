using Avalonia;
using Avalonia.Controls;

namespace EhViewerForPc.Controls;

/// <summary>
/// A single Fluent settings row: header text (plus optional description) on the
/// left, an arbitrary control on the right. The row stacks its action below the
/// label when the row itself becomes narrow, which is more accurate than
/// watching the main window width.
/// </summary>
public partial class SettingsRow : UserControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsRow, string?>(nameof(Description));

    public static readonly StyledProperty<object?> ActionProperty =
        AvaloniaProperty.Register<SettingsRow, object?>(nameof(Action));

    public static readonly DirectProperty<SettingsRow, bool> HasDescriptionProperty =
        AvaloniaProperty.RegisterDirect<SettingsRow, bool>(nameof(HasDescription), o => o.HasDescription);

    private bool _hasDescription;

    public SettingsRow()
    {
        InitializeComponent();
    }

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Control shown on the trailing edge (or below the label when narrow).</summary>
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    /// <summary>True when a description line is actually present.</summary>
    public bool HasDescription
    {
        get => _hasDescription;
        private set => SetAndRaise(HasDescriptionProperty, ref _hasDescription, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DescriptionProperty) HasDescription = !string.IsNullOrWhiteSpace(Description);
    }
}
