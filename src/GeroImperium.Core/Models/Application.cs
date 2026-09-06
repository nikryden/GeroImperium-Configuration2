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

    /// <summary>See doc/plan2.md's "skip if unchanged" open decision. True if any field (including the image)
    /// changed locally since the last successful push; set by every UpdateApplication call, cleared only by
    /// MarkApplicationSynced.</summary>
    public bool Dirty { get; set; } = true;

    /// <summary>The ImageChangedAtUtc value as of the last successful image upload -- lets Sync compare
    /// against the current ImageChangedAtUtc to skip re-uploading an unchanged 32768-byte image (the
    /// expensive part of a sync, per real hardware timeouts seen re-uploading unconditionally). Null means
    /// never pushed (or never had an image).</summary>
    public DateTime? LastSyncedImageChangedAtUtc { get; set; }
}
