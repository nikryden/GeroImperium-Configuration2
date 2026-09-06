using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GeroImperium.Core.Imaging;

/// <summary>
/// Single entry point every image-consuming feature (key editor, app editor, bulk export, incremental I/J
/// upload) should call: decode (PNG/JPEG via ImageSharp, SVG via SvgRasterizer) -> composite onto the chosen
/// background color -> convert to the device's wire format and/or a portable preview.
/// </summary>
public static class ImagePipeline
{
    public static byte[] ConvertToRgb565(byte[] imageBytes, Rgba32 backgroundColor)
    {
        using var composed = DecodeAndCompose(imageBytes, backgroundColor);
        return Rgb565Converter.Convert(composed);
    }

    /// <summary>
    /// Produces the two byte blobs the App's Applications/GeroImperiumKeys.ImageData(Rgb565) columns store:
    /// a composited 128x128 PNG (universally displayable, including for SVG sources WPF can't decode
    /// natively -- this is the App's own preview format, not something pushed to the device) and the
    /// matching device wire blob, both derived from the same composite so they never drift apart.
    /// </summary>
    public static (byte[] PreviewPng, byte[] Rgb565) ConvertToPreviewAndRgb565(byte[] imageBytes, Rgba32 backgroundColor)
    {
        using var composed = DecodeAndCompose(imageBytes, backgroundColor);

        using var pngStream = new MemoryStream();
        composed.SaveAsPng(pngStream);

        return (pngStream.ToArray(), Rgb565Converter.Convert(composed));
    }

    /// <summary>For pull-from-device: turns a downloaded device-format blob into a displayable PNG. There's no
    /// original source image to recomposite from later (the device only ever hands back the already-flattened
    /// wire format), so a pulled row's SourceImageData stays null -- a later background-color change on a
    /// pulled row requires picking a new source image, same as any other app-only column the device never
    /// round-trips.</summary>
    public static byte[] ConvertRgb565ToPreviewPng(byte[] rgb565)
    {
        using var image = Rgb565Converter.ConvertBack(rgb565);
        using var pngStream = new MemoryStream();
        image.SaveAsPng(pngStream);
        return pngStream.ToArray();
    }

    private static Image<Rgba32> DecodeAndCompose(byte[] imageBytes, Rgba32 backgroundColor)
    {
        using var source = IsSvg(imageBytes)
            ? SvgRasterizer.Rasterize(imageBytes)
            : Image.Load<Rgba32>(imageBytes);

        return ImageCompositor.ComposeOnBackground(source, backgroundColor);
    }

    private static bool IsSvg(byte[] bytes)
    {
        var probeLength = Math.Min(bytes.Length, 512);
        var text = Encoding.UTF8.GetString(bytes, 0, probeLength);
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }
}
