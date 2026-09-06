using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace GeroImperium.Core.Imaging;

/// <summary>
/// Converts a composited 128x128 image into the device's wire/storage format: each 8-bit RGB channel is
/// inverted (255-x), packed as B5G6R5 (blue in the high bits, red in the low bits -- not standard RGB565),
/// written little-endian, 2 bytes/pixel, row-major, no padding. Every other feature (key editor, app editor,
/// bulk export, incremental I/J upload) depends on this being exactly right -- see pc_app_integration.md
/// "Image format" and pc_app_plan.md's note to get this one piece right once.
/// </summary>
public static class Rgb565Converter
{
    public const int Width = 128;
    public const int Height = 128;
    public const int BlobSize = Width * Height * 2; // DB_IMAGE_BLOB_SIZE

    public static byte[] Convert(Image<Rgba32> image)
    {
        if (image.Width != Width || image.Height != Height)
        {
            throw new ArgumentException(
                $"Image must be exactly {Width}x{Height}, got {image.Width}x{image.Height}.", nameof(image));
        }

        var buffer = new byte[BlobSize];
        var offset = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    var invR = (byte)(255 - pixel.R);
                    var invG = (byte)(255 - pixel.G);
                    var invB = (byte)(255 - pixel.B);

                    var packed = (ushort)(((invB >> 3) << 11) | ((invG >> 2) << 5) | (invR >> 3));

                    buffer[offset++] = (byte)(packed & 0xFF); // low byte first (little-endian)
                    buffer[offset++] = (byte)(packed >> 8); // high byte
                }
            }
        });

        return buffer;
    }

    /// <summary>Inverse of <see cref="Convert"/> -- reconstructs a displayable image from a device-format blob
    /// (e.g. downloaded via GET .../image for a pull-from-device flow that has no original source image to
    /// recomposite from). Lossy: the 5/6-bit channels can't recover the original 8-bit precision Convert
    /// truncated away, but that's inherent to RGB565 and fine for a preview thumbnail.</summary>
    public static Image<Rgba32> ConvertBack(byte[] blob)
    {
        if (blob.Length != BlobSize)
        {
            throw new ArgumentException($"Blob must be exactly {BlobSize} bytes, got {blob.Length}.", nameof(blob));
        }

        var image = new Image<Rgba32>(Width, Height);
        image.ProcessPixelRows(accessor =>
        {
            var offset = 0;
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    ushort packed = (ushort)(blob[offset] | (blob[offset + 1] << 8));
                    offset += 2;

                    var invB = (byte)(((packed >> 11) & 0x1F) << 3);
                    var invG = (byte)(((packed >> 5) & 0x3F) << 2);
                    var invR = (byte)((packed & 0x1F) << 3);

                    row[x] = new Rgba32((byte)(255 - invR), (byte)(255 - invG), (byte)(255 - invB), 255);
                }
            }
        });

        return image;
    }
}
