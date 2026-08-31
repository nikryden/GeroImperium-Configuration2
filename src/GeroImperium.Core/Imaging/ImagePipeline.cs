using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GeroImperium.Core.Imaging;

/// <summary>
/// Single entry point every image-consuming feature (key editor, app editor, bulk export, incremental I/J
/// upload) should call: decode (PNG/JPEG via ImageSharp, SVG via SvgRasterizer) -> composite onto the chosen
/// background color -> convert to the device's wire format.
/// </summary>
public static class ImagePipeline
{
    public static byte[] ConvertToRgb565(byte[] imageBytes, Rgba32 backgroundColor)
    {
        using var source = IsSvg(imageBytes)
            ? SvgRasterizer.Rasterize(imageBytes)
            : Image.Load<Rgba32>(imageBytes);

        using var composed = ImageCompositor.ComposeOnBackground(source, backgroundColor);
        return Rgb565Converter.Convert(composed);
    }

    private static bool IsSvg(byte[] bytes)
    {
        var probeLength = Math.Min(bytes.Length, 512);
        var text = Encoding.UTF8.GetString(bytes, 0, probeLength);
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
