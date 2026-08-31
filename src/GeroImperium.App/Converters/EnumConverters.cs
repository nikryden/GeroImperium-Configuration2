using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GeroImperium.App.Converters;

/// <summary>Visible when the bound enum value equals the enum member named by ConverterParameter.</summary>
public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return Visibility.Collapsed;
        }

        var expected = Enum.Parse(value.GetType(), parameter.ToString()!);
        return Equals(value, expected) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
