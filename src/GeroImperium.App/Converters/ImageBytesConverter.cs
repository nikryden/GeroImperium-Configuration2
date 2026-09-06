using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GeroImperium.App.Converters;

/// <summary>Wraps a PNG/JPEG byte blob (e.g. Application/GeroImperiumKey.ImageData) as a WPF ImageSource.</summary>
public static class ImageBytesConverter
{
    public static ImageSource? ToImageSource(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes);
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>XAML-bindable wrapper around ImageBytesConverter -- for binding directly to a raw model's
/// ImageData (e.g. GeroImperium.Core.Models.Application), which has no pre-converted ImagePreview property
/// (that only exists on the App's own *ItemViewModel/*SlotViewModel wrappers).</summary>
public sealed class ImageBytesToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => ImageBytesConverter.ToImageSource(value as byte[]);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
