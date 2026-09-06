namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's Applications table. Device reads Id, ApplicationPageId, "Order", Name,
/// ImageDataRgb565 (preferred)/ImageData (fallback -- image bytes go over the separate image endpoints, not
/// this row's JSON). BackgroundColorArgb and SourceImageData are app-only columns the device ignores.
/// RemoteId is app-only: the row's Id on the device once pushed (see doc/plan2.md's diff-and-push sync model).
/// This app always writes ImageData/ImageDataRgb565 together via ImagePipeline.ConvertToPreviewAndRgb565:
/// ImageData holds the composited 128x128 PNG (this app's own displayable preview, not pushed to the device),
/// ImageDataRgb565 holds the device wire blob derived from the same composite, and SourceImageData keeps the
/// original pre-composite upload (which may still have transparency) so a later background-color change can
/// re-composite from scratch -- re-running the compositor on the already-flattened preview would be a no-op.
/// ImageChangedAtUtc is app-only: stamped whenever ImageData/ImageDataRgb565 is rewritten, so the sync path
/// can skip re-uploading the image bytes when this hasn't moved since the last push.
/// </summary>
public sealed class Application
{
    public long Id { get; set; }
    public long ApplicationPageId { get; set; }
    public string Order { get; set; } = "1";
    public string Name { get; set; } = string.Empty;
    public byte[]? ImageData { get; set; }
    public byte[]? ImageDataRgb565 { get; set; }
    public DateTime? ImageChangedAtUtc { get; set; }
    public int? BackgroundColorArgb { get; set; }
    public byte[]? SourceImageData { get; set; }
    public long? RemoteId { get; set; }
}
