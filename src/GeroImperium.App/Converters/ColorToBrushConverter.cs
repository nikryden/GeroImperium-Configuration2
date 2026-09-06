using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace GeroImperium.App.Converters;

public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new SolidColorBrush(value is Color color ? color : Colors.DimGray);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
