namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's Keys table (local table name kept as GeroImperiumKeys; REST path is the
/// case-insensitive "keys" -- see doc/plan2.md). Device reads KeyGroupId, Position (0-5), ImageDataRgb565
/// (preferred), ImageData (fallback), KeyActionId. BackgroundColorArgb and SourceImageData are app-only.
/// RemoteId is app-only: the row's Id on the device once pushed (see doc/plan2.md's diff-and-push sync model).
/// Like Application, ImageData/ImageDataRgb565/SourceImageData/ImageChangedAtUtc follow the same convention
/// -- see Application's doc comment.
/// </summary>
public sealed class GeroImperiumKey
{
    public long Id { get; set; }
    public long KeyGroupId { get; set; }
    public int Position { get; set; }
    public byte[]? ImageData { get; set; }
    public byte[]? ImageDataRgb565 { get; set; }
    public DateTime? ImageChangedAtUtc { get; set; }
    public long? KeyActionId { get; set; }
    public int? BackgroundColorArgb { get; set; }
    public byte[]? SourceImageData { get; set; }
    public long? RemoteId { get; set; }
}
