namespace GeroImperium.Core.Models;

/// <summary>
/// Mirrors the device's GeneralSettings table. Firmware only validates that the table exists on boot (E02 if
/// missing) and does not read any column from it -- all columns here are app-only.
/// </summary>
public sealed class GeneralSettings
{
    public long Id { get; set; }
    public string Theme { get; set; } = "Dark";
    public string? DefaultDevicePort { get; set; }
}
