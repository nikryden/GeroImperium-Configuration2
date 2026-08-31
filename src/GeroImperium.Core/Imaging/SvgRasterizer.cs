using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Svg.Skia;

namespace GeroImperium.Core.Imaging;

/// <summary>
/// Rasterizes an SVG to an RGBA bitmap, preserving transparency so ImageCompositor's background composite
/// step shows through.
/// </summary>
public static class SvgRasterizer
{
    public static Image<Rgba32> Rasterize(byte[] svgBytes, int width = Rgb565Converter.Width, int height = Rgb565Converter.Height)
    {
        using var svg = new SKSvg();
        using var stream = new MemoryStream(svgBytes);
        var picture = svg.Load(stream) ?? throw new InvalidDataException("Could not parse SVG.");

        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);

            var bounds = picture.CullRect;
            var scaleX = bounds.Width > 0 ? width / bounds.Width : 1f;
            var scaleY = bounds.Height > 0 ? height / bounds.Height : 1f;
            var scale = Math.Min(scaleX, scaleY);

            canvas.Translate((width - bounds.Width * scale) / 2f, (height - bounds.Height * scale) / 2f);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Flush();
        }

        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var c = bitmap.GetPixel(x, y);
                    row[x] = new Rgba32(c.Red, c.Green, c.Blue, c.Alpha);
                }
            }
        });

        return image;
    }
}
