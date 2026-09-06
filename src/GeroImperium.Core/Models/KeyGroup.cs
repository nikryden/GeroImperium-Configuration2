namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's KeyGroups table. Device reads Id, ApplicationId, "Order". Name is app-only.
/// RemoteId is app-only: the row's Id on the device once pushed (see doc/plan2.md's diff-and-push sync model).
/// </summary>
public sealed class KeyGroup
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string Order { get; set; } = "1";
    public string Name { get; set; } = string.Empty;
    public long? RemoteId { get; set; }

    /// <summary>See ApplicationPage.Dirty's doc comment -- same convention.</summary>
    public bool Dirty { get; set; } = true;
}
