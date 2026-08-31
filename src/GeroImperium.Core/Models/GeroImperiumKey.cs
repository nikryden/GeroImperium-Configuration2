namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's GeroImperiumKeys table. Device reads KeyGroupId, Position (0-5), ImageDataRgb565
/// (preferred), ImageData (fallback), KeyActionId. BackgroundColorArgb is app-only.
/// </summary>
public sealed class GeroImperiumKey
{
    public long Id { get; set; }
    public long KeyGroupId { get; set; }
    public int Position { get; set; }
    public byte[]? ImageData { get; set; }
    public byte[]? ImageDataRgb565 { get; set; }
    public long? KeyActionId { get; set; }
    public int? BackgroundColorArgb { get; set; }
}
