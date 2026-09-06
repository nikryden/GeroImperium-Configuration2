using GeroImperium.Core.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GeroImperium.Core.Tests;

public class Rgb565ConverterTests
{
    [Fact]
    public void BlobSize_Is32768()
    {
        Assert.Equal(32768, Rgb565Converter.BlobSize);
    }

    [Fact]
    public void Convert_WrongDimensions_Throws()
    {
        using var image = new Image<Rgba32>(64, 64);
        Assert.Throws<ArgumentException>(() => Rgb565Converter.Convert(image));
    }

    [Theory]
    // (R, G, B) -> expected little-endian byte pair after invert-then-pack-B5G6R5.
    // Hand-computed: invert each channel (255-x), then packed = (invB>>3)<<11 | (invG>>2)<<5 | (invR>>3).
    [InlineData((byte)255, (byte)0, (byte)0, (byte)0xE0, (byte)0xFF)] // red -> inverted (0,255,255) -> 0xFFE0
    [InlineData((byte)0, (byte)0, (byte)0, (byte)0xFF, (byte)0xFF)] // black -> inverted (255,255,255) -> 0xFFFF
    [InlineData((byte)255, (byte)255, (byte)255, (byte)0x00, (byte)0x00)] // white -> inverted (0,0,0) -> 0x0000
    [InlineData((byte)128, (byte)128, (byte)128, (byte)0xEF, (byte)0x7B)] // mid-gray -> inverted (127,127,127) -> 0x7BEF
    public void Convert_SolidColor_MatchesHandComputedBytes(byte r, byte g, byte b, byte expectedLow, byte expectedHigh)
    {
        using var image = new Image<Rgba32>(Rgb565Converter.Width, Rgb565Converter.Height, new Rgba32(r, g, b));

        var result = Rgb565Converter.Convert(image);

        Assert.Equal(Rgb565Converter.BlobSize, result.Length);
        for (var i = 0; i < result.Length; i += 2)
        {
            Assert.Equal(expectedLow, result[i]);
            Assert.Equal(expectedHigh, result[i + 1]);
        }
    }

    [Fact]
    public void Convert_Checkerboard_AlternatesPerPixel()
    {
        using var image = new Image<Rgba32>(Rgb565Converter.Width, Rgb565Converter.Height);
        var red = new Rgba32(255, 0, 0);
        var black = new Rgba32(0, 0, 0);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = (x + y) % 2 == 0 ? red : black;
                }
            }
        });

        var result = Rgb565Converter.Convert(image);

        // red -> 0xFFE0 (bytes E0 FF), black -> 0xFFFF (bytes FF FF)
        for (var y = 0; y < Rgb565Converter.Height; y++)
        {
            for (var x = 0; x < Rgb565Converter.Width; x++)
            {
                var offset = (y * Rgb565Converter.Width + x) * 2;
                var isRed = (x + y) % 2 == 0;
                Assert.Equal(isRed ? (byte)0xE0 : (byte)0xFF, result[offset]);
                Assert.Equal((byte)0xFF, result[offset + 1]);
            }
        }
    }

    [Fact]
    public void ConvertBack_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => Rgb565Converter.ConvertBack(new byte[100]));
    }

    [Fact]
    public void ConvertBack_White_RoundTripsExactly()
    {
        // White is the one color losslessly safe through invert-then-truncate regardless of channel bit
        // depth: 255 inverts to 0, and 0 truncates/expands back to 0 at any bit width.
        using var original = new Image<Rgba32>(Rgb565Converter.Width, Rgb565Converter.Height, new Rgba32(255, 255, 255));

        var blob = Rgb565Converter.Convert(original);
        using var roundTripped = Rgb565Converter.ConvertBack(blob);

        roundTripped.ProcessPixelRows(accessor =>
        {
            var row = accessor.GetRowSpan(0);
            Assert.Equal((byte)255, row[0].R);
            Assert.Equal((byte)255, row[0].G);
            Assert.Equal((byte)255, row[0].B);
            Assert.Equal((byte)255, row[0].A);
        });
    }

    [Theory]
    [InlineData((byte)0, (byte)0, (byte)0)]
    [InlineData((byte)255, (byte)0, (byte)0)]
    [InlineData((byte)128, (byte)128, (byte)128)]
    public void ConvertBack_IsStable_ReEncodingProducesTheSameBlob(byte r, byte g, byte b)
    {
        // General colors lose precision going through Convert's 5/6-bit truncation (expected -- that's RGB565),
        // so ConvertBack won't reproduce the exact original pixels. What matters for a pull-from-device round
        // trip is stability: once quantized, re-encoding the reconstructed image must land on the same grid
        // point, not drift further -- otherwise a pulled-then-never-edited image would look "changed" forever.
        using var original = new Image<Rgba32>(Rgb565Converter.Width, Rgb565Converter.Height, new Rgba32(r, g, b));
        var blob = Rgb565Converter.Convert(original);

        using var roundTripped = Rgb565Converter.ConvertBack(blob);
        var reEncoded = Rgb565Converter.Convert(roundTripped);

        Assert.Equal(blob, reEncoded);
    }

    [Fact]
    public void ConvertBack_ProducesCorrectDimensions()
    {
        using var original = new Image<Rgba32>(Rgb565Converter.Width, Rgb565Converter.Height, new Rgba32(10, 20, 30));
        var blob = Rgb565Converter.Convert(original);

        using var roundTripped = Rgb565Converter.ConvertBack(blob);

        Assert.Equal(Rgb565Converter.Width, roundTripped.Width);
        Assert.Equal(Rgb565Converter.Height, roundTripped.Height);
    }
}
