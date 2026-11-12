using Avalonia;
using Avalonia.Controls;

namespace EhViewerReader.Controls;

/// <summary>Stacks a small number of detail panes when their columns no longer fit.</summary>
public sealed class AdaptiveGrid : Grid
{
    /// <summary>Width below which all children are stacked in a single column.</summary>
    public static readonly StyledProperty<double> BreakpointProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(Breakpoint), 740d);

    private GridLength[]? _columns;
    private GridLength[]? _rows;
    private (Control Control, int Row, int Column, int RowSpan, int ColumnSpan)[] _positions = [];
    private bool? _stacked;

    public double Breakpoint
    {
        get => GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_columns is null)
        {
            _columns = ColumnDefinitions.Select(c => c.Width).ToArray();
            _rows = RowDefinitions.Select(r => r.Height).ToArray();
            _positions = Children.Select(c => (c, GetRow(c), GetColumn(c), GetRowSpan(c), GetColumnSpan(c))).ToArray();
        }
        bool stacked = e.NewSize.Width < Breakpoint;
        if (_stacked == stacked) return;
        _stacked = stacked;
        ColumnDefinitions.Clear(); RowDefinitions.Clear();
        if (stacked)
        {
            ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            for (int i = 0; i < _positions.Length; i++)
            {
                RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var item = _positions[i]; SetColumn(item.Control, 0); SetRow(item.Control, i); SetRowSpan(item.Control, 1); SetColumnSpan(item.Control, 1);
            }
        }
        else
        {
            foreach (var column in _columns) ColumnDefinitions.Add(new ColumnDefinition(column));
            foreach (var row in _rows!) RowDefinitions.Add(new RowDefinition(row));
            foreach (var item in _positions) { SetColumn(item.Control, item.Column); SetRow(item.Control, item.Row); SetRowSpan(item.Control, item.RowSpan); SetColumnSpan(item.Control, item.ColumnSpan); }
        }
    }
}
