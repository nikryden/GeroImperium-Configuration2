namespace GeroImperium.Core.Models;

/// <summary>A tombstone for a row deleted locally that already existed on the device (RemoteId was set at
/// delete time). Sync issues a DELETE for TableName/RemoteId and removes the tombstone on success -- see
/// doc/plan2.md's "Tombstone tracking for deletes" open decision. Only top-level user-deletable entities
/// (ApplicationPages, Applications, KeyGroups) ever produce one: the device cascades deletes per FK (per
/// doc/windows_app_api_guide.md), so deleting a page's remote row already removes its applications/key
/// groups/keys remotely too -- no need to also tombstone every descendant.</summary>
public sealed class PendingDelete
{
    public long Id { get; set; }
    public string TableName { get; set; } = string.Empty;
    public long RemoteId { get; set; }
}
