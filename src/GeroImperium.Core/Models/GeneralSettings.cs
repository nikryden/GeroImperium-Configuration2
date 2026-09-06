namespace GeroImperium.Core.Models;

/// <summary>
/// App-local-only settings -- GeneralSettings is not one of the device's real REST tables (see
/// doc/windows_app_api_guide.md, doc/plan2.md), unlike the old CDC-era assumption that it round-tripped to
/// the device. DeviceIpAddress is the last-known REST base address (learned from GET /api/status or the BLE
/// WiFi-status characteristic); LastKnownBleDeviceId helps re-find a previously-paired device.
/// </summary>
public sealed class GeneralSettings
{
    public long Id { get; set; }
    public string Theme { get; set; } = "Dark";
    public string? DeviceIpAddress { get; set; }
    public string? LastKnownBleDeviceId { get; set; }
}
