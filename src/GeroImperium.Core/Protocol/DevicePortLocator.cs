using System.Management;

namespace GeroImperium.Core.Protocol;

/// <summary>
/// Locates the device's CDC-ACM COM port(s) by USB hardware ID rather than the generic driver-assigned
/// friendly name. Win32_SerialPort exposes both DeviceID ("COM5") and PNPDeviceID directly, so no registry
/// or Win32_PnPEntity name-string parsing is needed -- see pc_app_integration.md "Finding the right COM port".
/// </summary>
public static class DevicePortLocator
{
    public static IReadOnlyList<string> FindDevicePorts()
    {
        var ports = new List<string>();

        //using var searcher = new ManagementObjectSearcher(
        //    "SELECT DeviceID, PNPDeviceID FROM Win32_SerialPort");
        using var searcher = new ManagementObjectSearcher(
                    $"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%' AND PNPDeviceID LIKE '%{DeviceIdentity.PnpDeviceIdFragment}%'");

        foreach (ManagementBaseObject entity in searcher.Get())
        {
            var searchResult = entity.ToString();

            var name = entity["Name"] as string ?? string.Empty;

            var portName = ExtractComPortName(name);
            if (portName == null)
                continue;

            ports.Add(portName);

            //using (item)
            // {
            //    //var devid = item.GetPropertyValue("PNPDeviceID");
            //    //var pnpDeviceId = item["PNPDeviceID"] as string ?? string.Empty;
            //    //if (pnpDeviceId.Contains(DeviceIdentity.PnpDeviceIdFragment, StringComparison.OrdinalIgnoreCase)
            //    //    && item["DeviceID"] is string deviceId)
            //    //{
            //    //    ports.Add(deviceId);
            //    //}
            //}
        }

        return ports;
    }

    private static string? ExtractComPortName(string pnpFriendlyName)
    {
        int start = pnpFriendlyName.LastIndexOf("(COM", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        int end = pnpFriendlyName.IndexOf(')', start);
        if (end < 0)
            return null;

        return pnpFriendlyName.Substring(start + 1, end - start - 1);
    }
}
