using System.IO;
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
