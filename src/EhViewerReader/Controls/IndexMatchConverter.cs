using System.Globalization;
using Avalonia.Data.Converters;

namespace EhViewerReader.Controls;

/// <summary>
/// True when the bound index equals the converter parameter. Used by pages that
/// keep a single selected index but show one panel per index, without bringing
/// in an extra selection model.
/// </summary>
public sealed class IndexMatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        return value is int index
            ? int.TryParse(parameter.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int expected) && index == expected
            : false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
