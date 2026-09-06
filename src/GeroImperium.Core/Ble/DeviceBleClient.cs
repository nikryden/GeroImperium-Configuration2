using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace GeroImperium.Core.Ble;

/// <summary>BLE connection + GATT discovery for the device, ported from GeroImperium.WifiProvisionCli's
/// FindDeviceAsync/GetAllServicesWithRetryAsync (doc/windows_app_api_guide.md Part 2 says to reuse that
/// project directly -- it's a working, hardware-tested reference, not a throwaway). WifiProvisioningService
/// and AppLaunchService both drive a connected instance of this rather than talking to WinRT types directly.</summary>
public sealed class DeviceBleClient : IDisposable
{
    public const string DeviceName = "GeroImperium";

    private static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(15);

    private BluetoothLEDevice? _device;

    public bool IsConnected => _device is not null && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>Cheap presence check -- true if the device shows up in Windows' already-paired BLE device
    /// list, with no GATT connection opened. Purely a "reconnect via Bluetooth is likely to work" signal for
    /// a connectivity indicator (doc/plan2.md's "Device discovery & the 'connected' indicator") -- distinct
    /// from ConnectAsync, which actually opens the connection.</summary>
    public static async Task<bool> IsPairedAsync(CancellationToken ct = default)
    {
        DeviceInformationCollection known = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector())
            .AsTask(ct).ConfigureAwait(false);
        return known.Any(d => string.Equals(d.Name, DeviceName, StringComparison.Ordinal));
    }

    /// <summary>Finds the device two ways, fast path first: already-known/paired (no advertising needed --
    /// and once paired, NimBLE stops advertising entirely since CONFIG_BT_NIMBLE_MAX_CONNECTIONS=1, so a live
    /// scan would never find an already-connected device), else falls back to watching for its advertisement
    /// (device must be in its manual pairing window -- nav-key1 held 10s). Returns false, does not throw, if
    /// the device can't be found within scanTimeout -- a normal "not reachable right now" outcome.</summary>
    public async Task<bool> ConnectAsync(TimeSpan? scanTimeout = null, CancellationToken ct = default)
    {
        _device?.Dispose();
        _device = await FindDeviceAsync(DeviceName, scanTimeout ?? DefaultScanTimeout, ct).ConfigureAwait(false);
        if (_device is null)
        {
            return false;
        }

        // A BluetoothLEDevice handle existing doesn't mean the physical link is actually up -- Windows often
        // only really negotiates the connection on the first GATT call, which is exactly where a transient
        // COMException (commonly 0x80070016) tends to fire. This settle delay measurably helps (hardware-
        // confirmed in WifiProvisionCli); BleRetry above is the backstop for whatever it doesn't catch.
        await Task.Delay(1000, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Deliberately GetGattServicesAsync (discover-all) + client-side UUID filter, NOT
    /// GetGattServicesForUuidAsync -- the latter uses ATT "Find By Type Value" under the hood, which reliably
    /// throws COMException 0x80070016 against this device's 128-bit custom service UUIDs (confirmed bug,
    /// doc/windows_app_api_guide.md Part 2's GATT discovery gotcha).</summary>
    public async Task<GattDeviceService?> GetServiceAsync(Guid serviceUuid, CancellationToken ct = default)
    {
        if (_device is null)
        {
            throw new InvalidOperationException("Not connected -- call ConnectAsync first.");
        }

        GattDeviceServicesResult result = await BleRetry.RunAsync(
            () => _device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct), r => r.Status, ct: ct).ConfigureAwait(false);
        return result.Status == GattCommunicationStatus.Success
            ? result.Services.FirstOrDefault(s => s.Uuid == serviceUuid)
            : null;
    }

    /// <summary>Same discover-all-then-filter reasoning as GetServiceAsync, applied to characteristics.</summary>
    public async Task<GattCharacteristic?> GetCharacteristicAsync(GattDeviceService service, Guid characteristicUuid, CancellationToken ct = default)
    {
        GattCharacteristicsResult result = await BleRetry.RunAsync(
            () => service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(ct), r => r.Status, ct: ct).ConfigureAwait(false);
        return result.Status == GattCommunicationStatus.Success
            ? result.Characteristics.FirstOrDefault(c => c.Uuid == characteristicUuid)
            : null;
    }

    private static async Task<BluetoothLEDevice?> FindDeviceAsync(string name, TimeSpan scanTimeout, CancellationToken ct)
    {
        DeviceInformationCollection known = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector())
            .AsTask(ct).ConfigureAwait(false);
        DeviceInformation? knownMatch = known.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
        if (knownMatch is not null)
        {
            return await BluetoothLEDevice.FromIdAsync(knownMatch.Id).AsTask(ct).ConfigureAwait(false);
        }

        ulong? address = await ScanForDeviceAsync(name, scanTimeout, ct).ConfigureAwait(false);
        return address is null ? null : await BluetoothLEDevice.FromBluetoothAddressAsync(address.Value).AsTask(ct).ConfigureAwait(false);
    }

    private static async Task<ulong?> ScanForDeviceAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var tcs = new TaskCompletionSource<ulong?>();

        watcher.Received += (_, eventArgs) =>
        {
            if (string.Equals(eventArgs.Advertisement.LocalName, name, StringComparison.Ordinal))
            {
                tcs.TrySetResult(eventArgs.BluetoothAddress);
            }
        };

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        using CancellationTokenRegistration registration = timeoutCts.Token.Register(() => tcs.TrySetResult(null));

        watcher.Start();
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
        }
    }

    public void Dispose() => _device?.Dispose();
}
