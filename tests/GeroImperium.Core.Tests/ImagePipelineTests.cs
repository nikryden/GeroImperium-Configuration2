using GeroImperium.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GeroImperium.Core.Tests;

public class ImagePipelineTests
{
    [Fact]
    public void ConvertToRgb565_Png_ProducesCorrectLengthAndColor()
    {
        using var source = new Image<Rgba32>(32, 32, new Rgba32(0, 0, 255)); // blue, smaller than 128x128
        using var stream = new MemoryStream();
        source.SaveAsPng(stream);
        var pngBytes = stream.ToArray();

        var result = ImagePipeline.ConvertToRgb565(pngBytes, new Rgba32(0, 0, 255));

        Assert.Equal(Rgb565Converter.BlobSize, result.Length);

        // blue (0,0,255) inverted -> (255,255,0) -> packed = (invB=0)>>3<<11 | (invG=255)>>2<<5 | (invR=255)>>3
        //   = 0x07E0 | 0x1F = 0x07FF -> little-endian bytes FF 07. Source fills the whole 128x128 canvas
        // (aspect ratio matches container, background is the same color), so this holds for every pixel.
        Assert.Equal((byte)0xFF, result[0]);
        Assert.Equal((byte)0x07, result[1]);
    }

    [Fact]
    public void ConvertToRgb565_Svg_ProducesCorrectLength()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
              <rect width="64" height="64" fill="#ff0000"/>
            </svg>
            """;
        var svgBytes = System.Text.Encoding.UTF8.GetBytes(svg);

        var result = ImagePipeline.ConvertToRgb565(svgBytes, new Rgba32(0, 0, 0));

        Assert.Equal(Rgb565Converter.BlobSize, result.Length);
        Assert.Contains(result, b => b != 0); // sanity: not a degenerate all-zero blob
    }

    [Fact]
    public void ConvertToPreviewAndRgb565_ReturnsMatchingPngAndWireBytes()
    {
        using var source = new Image<Rgba32>(32, 32, new Rgba32(0, 0, 255));
        using var stream = new MemoryStream();
        source.SaveAsPng(stream);
        var pngBytes = stream.ToArray();

        var (previewPng, rgb565) = ImagePipeline.ConvertToPreviewAndRgb565(pngBytes, new Rgba32(0, 0, 255));

        Assert.Equal(Rgb565Converter.BlobSize, rgb565.Length);
        Assert.Equal(rgb565, ImagePipeline.ConvertToRgb565(pngBytes, new Rgba32(0, 0, 255)));

        // The returned preview must itself be a valid, decodable 128x128 PNG.
        using var decodedPreview = Image.Load<Rgba32>(previewPng);
        Assert.Equal(128, decodedPreview.Width);
        Assert.Equal(128, decodedPreview.Height);
        Assert.Equal(new Rgba32(0, 0, 255), decodedPreview[0, 0]);
    }

    [Fact]
    public void ConvertToPreviewAndRgb565_Svg_ProducesDecodablePreview()
    {
        const string svg = """
            <svg xmlns="http://www.w3.org/2000/svg" width="64" height="64" viewBox="0 0 64 64">
              <rect width="64" height="64" fill="#00ff00"/>
            </svg>
            """;
        var svgBytes = System.Text.Encoding.UTF8.GetBytes(svg);

        var (previewPng, rgb565) = ImagePipeline.ConvertToPreviewAndRgb565(svgBytes, new Rgba32(0, 0, 0));

        Assert.Equal(Rgb565Converter.BlobSize, rgb565.Length);
        using var decodedPreview = Image.Load<Rgba32>(previewPng);
        Assert.Equal(128, decodedPreview.Width);
        Assert.Equal(128, decodedPreview.Height);
    }
}
