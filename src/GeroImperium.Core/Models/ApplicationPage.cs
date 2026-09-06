namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's ApplicationPages table (`GET/POST /api/applicationpages`) -- a "page" of up to 6
/// Applications shown together. Device reads Id, "Order", Name. RemoteId is app-only: the row's Id on the
/// device once pushed, used by the diff-and-push sync model (see doc/plan2.md) to decide POST vs. PUT.
/// </summary>
public sealed class ApplicationPage
{
    public long Id { get; set; }
    public string Order { get; set; } = "1";
    public string Name { get; set; } = string.Empty;
    public long? RemoteId { get; set; }

    /// <summary>True if edited locally since the last successful push -- lets Sync skip an unchanged,
    /// already-pushed row instead of unconditionally re-sending it (doc/plan2.md's "skip if unchanged" open
    /// decision). Set by every GeroImperiumRepository.UpdateApplicationPage call; cleared only by
    /// MarkApplicationPageSynced after a successful push.</summary>
    public bool Dirty { get; set; } = true;
}
