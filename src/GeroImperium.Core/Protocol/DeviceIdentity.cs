namespace GeroImperium.Core.Protocol;

/// <summary>
/// USB hardware ID the device enumerates under. Windows' inbox CDC-ACM driver hides the device's own
/// iManufacturer/iProduct strings behind a generic "USB Serial Device" name, so the PC app must match on
/// VID/PID rather than the friendly name -- see pc_app_integration.md "Finding the right COM port".
/// </summary>
public static class DeviceIdentity
{
    public const int VendorId = 0x303A;
    public const int ProductId = 0x8001;
    public const string PnpDeviceIdFragment = "VID_303A&PID_8001";
}
