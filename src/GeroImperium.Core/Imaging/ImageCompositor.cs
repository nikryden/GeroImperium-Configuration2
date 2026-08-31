using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GeroImperium.Core.Imaging;

/// <summary>
/// Composites a source image onto a solid background color and letterboxes it to exactly 128x128 -- the
/// device only stores a flat RGB blob per slot, so background color is an authoring-time compositing step,
/// not a device feature (see pc_app_plan.md "Data model decisions").
/// </summary>
public static class ImageCompositor
{
    public static Image<Rgba32> ComposeOnBackground(Image<Rgba32> source, Rgba32 backgroundColor)
    {
        var canvas = new Image<Rgba32>(Rgb565Converter.Width, Rgb565Converter.Height, backgroundColor);

        using var resized = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(Rgb565Converter.Width, Rgb565Converter.Height),
            Mode = ResizeMode.Max,
        }));

        var location = new Point(
            (Rgb565Converter.Width - resized.Width) / 2,
            (Rgb565Converter.Height - resized.Height) / 2);

        canvas.Mutate(ctx => ctx.DrawImage(resized, location, 1f));

        return canvas;
    }
}
