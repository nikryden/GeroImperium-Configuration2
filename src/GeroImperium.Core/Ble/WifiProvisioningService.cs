using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace GeroImperium.Core.Ble;

/// <summary>wifi_manager.h's wifi_mgr_state_t, in the same order as the status blob's byte 0.</summary>
public enum WifiProvisioningState
{
    Idle = 0,
    Connecting = 1,
    Connected = 2,
    ApFallback = 3,
}

/// <summary>Parsed 7-byte WiFi-status blob (doc/windows_app_api_guide.md Part 2, wifi_manager_get_status_blob()).
/// Rssi is meaningless unless State == Connected.</summary>
public sealed record WifiStatus(WifiProvisioningState State, byte RetryCount, sbyte Rssi, string IpAddress)
{
    public bool IsConnected => State == WifiProvisioningState.Connected;
}

/// <summary>WiFi-Provisioning BLE GATT service (doc/windows_app_api_guide.md Part 2). UUIDs are project-defined
/// (not Bluetooth SIG assigned), copied verbatim from the guide -- must match connectivity_manager.c's
/// GATT_SVC_WIFI_PROV_UUID and friends.</summary>
public sealed class WifiProvisioningService
{
    public static readonly Guid ServiceUuid = new("6E5A0001-3AE4-4D19-B7B0-4C1A2E9F0A10");
    private static readonly Guid SsidCharUuid = new("6E5A0002-3AE4-4D19-B7B0-4C1A2E9F0A10");
    private static readonly Guid PasswordCharUuid = new("6E5A0003-3AE4-4D19-B7B0-4C1A2E9F0A10");
    private static readonly Guid ConnectCharUuid = new("6E5A0004-3AE4-4D19-B7B0-4C1A2E9F0A10");
    private static readonly Guid StatusCharUuid = new("6E5A0005-3AE4-4D19-B7B0-4C1A2E9F0A10");

    private readonly DeviceBleClient _client;

    public WifiProvisioningService(DeviceBleClient client) => _client = client;

    /// <summary>Writes SSID, then password, then the connect-trigger, strictly in that order -- the guide is
    /// explicit the order matters: SSID/password only stage locally on-device, and it's the connect-trigger
    /// write (write-without-response, value ignored) that actually calls wifi_manager_set_credentials() and
    /// kicks off the connection attempt. Returns false, does not throw, if the service/characteristics can't
    /// be found or a write fails -- callers should treat that as "provisioning didn't start".</summary>
    public async Task<bool> ProvisionAsync(string ssid, string password, CancellationToken ct = default)
    {
        GattDeviceService? service = await _client.GetServiceAsync(ServiceUuid, ct).ConfigureAwait(false);
        if (service is null)
        {
            return false;
        }

        return await WriteAsync(service, SsidCharUuid, Encoding.UTF8.GetBytes(ssid), GattWriteOption.WriteWithResponse, ct).ConfigureAwait(false)
            && await WriteAsync(service, PasswordCharUuid, Encoding.UTF8.GetBytes(password), GattWriteOption.WriteWithResponse, ct).ConfigureAwait(false)
            && await WriteAsync(service, ConnectCharUuid, [0x01], GattWriteOption.WriteWithoutResponse, ct).ConfigureAwait(false);
    }

    /// <summary>One-shot read of the status characteristic. Returns null if the service/characteristic can't
    /// be found, the read fails, or the blob is shorter than 7 bytes.</summary>
    public async Task<WifiStatus?> ReadStatusAsync(CancellationToken ct = default)
    {
        GattDeviceService? service = await _client.GetServiceAsync(ServiceUuid, ct).ConfigureAwait(false);
        GattCharacteristic? statusChar = service is null
            ? null
            : await _client.GetCharacteristicAsync(service, StatusCharUuid, ct).ConfigureAwait(false);
        if (statusChar is null)
        {
            return null;
        }

        GattReadResult result = await BleRetry.RunAsync(
            () => statusChar.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct), r => r.Status, ct: ct).ConfigureAwait(false);
        return result.Status == GattCommunicationStatus.Success ? ParseStatusBlob(result.Value.ToArray()) : null;
    }

    /// <summary>Polls once a second (simpler than notify-subscribe for a one-shot first-run flow) until
    /// State == Connected or timeout elapses. Returns the last status read (possibly not Connected) on
    /// timeout, or null if the status characteristic could never be read at all.</summary>
    public async Task<WifiStatus?> PollUntilConnectedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        WifiStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ReadStatusAsync(ct).ConfigureAwait(false);
            if (last is { IsConnected: true })
            {
                return last;
            }

            await Task.Delay(1000, ct).ConfigureAwait(false);
        }

        return last;
    }

    private async Task<bool> WriteAsync(GattDeviceService service, Guid characteristicUuid, byte[] value,
        GattWriteOption option, CancellationToken ct)
    {
        GattCharacteristic? characteristic = await _client.GetCharacteristicAsync(service, characteristicUuid, ct).ConfigureAwait(false);
        if (characteristic is null)
        {
            return false;
        }

        GattCommunicationStatus status = await BleRetry.RunAsync(
            () => characteristic.WriteValueAsync(value.AsBuffer(), option).AsTask(ct), ct: ct).ConfigureAwait(false);
        return status == GattCommunicationStatus.Success;
    }

    /// <summary>Byte layout per the guide: [0]=state, [1]=retry count, [2]=signed RSSI, [3..6]=IPv4 in
    /// dotted-decimal order (byte3 is the 1st octet) -- NOT raw little-endian machine order.</summary>
    private static WifiStatus? ParseStatusBlob(byte[] blob)
    {
        if (blob.Length < 7)
        {
            return null;
        }

        var state = (WifiProvisioningState)blob[0];
        sbyte rssi = unchecked((sbyte)blob[2]);
        string ip = $"{blob[3]}.{blob[4]}.{blob[5]}.{blob[6]}";
        return new WifiStatus(state, blob[1], rssi, ip);
    }
}
