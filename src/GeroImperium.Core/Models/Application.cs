namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's Applications table. Device reads Id, ImageDataRgb565 (preferred), ImageData (fallback).
/// Name, BackgroundColorArgb, and SourceImageData are app-only columns the device ignores.
/// This app always writes ImageData/ImageDataRgb565 together via ImagePipeline.ConvertToPreviewAndRgb565:
/// ImageData holds the composited 128x128 PNG (this app's own displayable preview, not pushed to the device),
/// ImageDataRgb565 holds the device wire blob derived from the same composite, and SourceImageData keeps the
/// original pre-composite upload (which may still have transparency) so a later background-color change can
/// re-composite from scratch -- re-running the compositor on the already-flattened preview would be a no-op.
/// </summary>
public sealed class Application
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public byte[]? ImageData { get; set; }
    public byte[]? ImageDataRgb565 { get; set; }
    public int? BackgroundColorArgb { get; set; }
    public byte[]? SourceImageData { get; set; }
}
