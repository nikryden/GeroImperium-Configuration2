namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's KeyGroups table. Device reads Id, ApplicationId. Name is app-only.
/// </summary>
public sealed class KeyGroup
{
    public long Id { get; set; }
    public long ApplicationId { get; set; }
    public string Name { get; set; } = string.Empty;
}
