namespace GeroImperium.Core.Http;

/// <summary>
/// Wire shapes for the device's 5 REST tables (doc/windows_app_api_guide.md) -- deliberately separate from
/// Core.Models' local authoring types, which carry app-only columns (ImageData, SourceImageData, RemoteId,
/// ...) the device has never heard of and would reject/ignore if round-tripped verbatim. Mapping between the
/// two is sync-layer work (doc/plan2.md phase 12.6), not this client's job.
/// Every row also carries CreatedAt/UpdatedAt (ISO 8601 strings) -- currently wrong on-device (no RTC/NTP,
/// see the guide's Gotchas), kept here only so deserialization doesn't choke on the field, not for display.
/// </summary>
public sealed record ApplicationPageRow(long Id, string Order, string Name, string? CreatedAt, string? UpdatedAt);

public sealed record ApplicationRow(long Id, long ApplicationPageId, string Order, string Name, ushort? BackgroundColor, string? CreatedAt, string? UpdatedAt);

public sealed record KeyGroupRow(long Id, long ApplicationId, string Order, string Name, string? CreatedAt, string? UpdatedAt);

public sealed record KeyActionRow(long Id, int Type, string? TextContent, string? CreatedAt, string? UpdatedAt);

public sealed record KeyRow(long Id, long KeyGroupId, long? KeyActionId, int Position, ushort? BackgroundColor, string? CreatedAt, string? UpdatedAt);
