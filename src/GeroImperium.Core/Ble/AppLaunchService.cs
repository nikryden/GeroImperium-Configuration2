using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace GeroImperium.Core.Ble;

/// <summary>App-Launch BLE GATT service (doc/windows_app_api_guide.md Part 2, added 2026-09-05) -- fires when
/// the user taps an app icon on the device itself (a Level-1 action-key press). UUIDs are project-defined,
/// copied verbatim from the guide.</summary>
public sealed class AppLaunchService : IDisposable
{
    public static readonly Guid ServiceUuid = new("6E5A0006-3AE4-4D19-B7B0-4C1A2E9F0A10");
    private static readonly Guid AppIdCharUuid = new("6E5A0007-3AE4-4D19-B7B0-4C1A2E9F0A10");

    private readonly DeviceBleClient _client;
    private GattCharacteristic? _subscribedCharacteristic;

    public AppLaunchService(DeviceBleClient client) => _client = client;

    /// <summary>Raised with the pressed app's applications.Id (the real database Id, matching the REST API's
    /// Id field -- not a page-relative position) each time the device notifies. Look up the row via
    /// GET /api/applications/{id} to resolve what to launch/focus. Only fires after a successful
    /// SubscribeAsync.</summary>
    public event EventHandler<ushort>? AppLaunched;

    /// <summary>Reads the App Id characteristic once without subscribing -- e.g. to pick up a press that
    /// happened before this process connected. Returns null if the service/characteristic can't be found or
    /// the read fails.</summary>
    public async Task<ushort?> ReadOnceAsync(CancellationToken ct = default)
    {
        GattCharacteristic? characteristic = await GetCharacteristicAsync(ct).ConfigureAwait(false);
        if (characteristic is null)
        {
            return null;
        }

        GattReadResult result = await BleRetry.RunAsync(
            () => characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct), r => r.Status, ct: ct).ConfigureAwait(false);
        return result.Status == GattCommunicationStatus.Success ? ParseAppId(result.Value.ToArray()) : null;
    }

    /// <summary>Subscribes to notify -- AppLaunched fires for every subsequent press while connected and
    /// subscribed. Call UnsubscribeAsync before handing the BLE connection to provisioning (this service and
    /// WifiProvisioningService can never both hold the device's one GATT connection slot at once -- see
    /// doc/plan2.md's "Why the Service still exists").</summary>
    public async Task<bool> SubscribeAsync(CancellationToken ct = default)
    {
        GattCharacteristic? characteristic = await GetCharacteristicAsync(ct).ConfigureAwait(false);
        if (characteristic is null)
        {
            return false;
        }

        characteristic.ValueChanged += OnValueChanged;

        GattCommunicationStatus status = await BleRetry.RunAsync(
            () => characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct), ct: ct).ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= OnValueChanged;
            return false;
        }

        _subscribedCharacteristic = characteristic;
        return true;
    }

    public async Task UnsubscribeAsync(CancellationToken ct = default)
    {
        if (_subscribedCharacteristic is not { } characteristic)
        {
            return;
        }

        _subscribedCharacteristic = null;
        characteristic.ValueChanged -= OnValueChanged;
        await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(ct).ConfigureAwait(false);
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (ParseAppId(args.CharacteristicValue.ToArray()) is { } appId)
        {
            AppLaunched?.Invoke(this, appId);
        }
    }

    private async Task<GattCharacteristic?> GetCharacteristicAsync(CancellationToken ct)
    {
        GattDeviceService? service = await _client.GetServiceAsync(ServiceUuid, ct).ConfigureAwait(false);
        return service is null ? null : await _client.GetCharacteristicAsync(service, AppIdCharUuid, ct).ConfigureAwait(false);
    }

    /// <summary>uint16 little-endian per the guide -- the real applications.Id, not a page-relative position.</summary>
    private static ushort? ParseAppId(byte[] value) => value.Length >= 2 ? BitConverter.ToUInt16(value, 0) : null;

    /// <summary>Detaches the local event handler only -- does not tell the device to stop notifying. Call
    /// UnsubscribeAsync first for a clean handoff; this is just a safety net against a leaked handler.</summary>
    public void Dispose()
    {
        if (_subscribedCharacteristic is { } characteristic)
        {
            characteristic.ValueChanged -= OnValueChanged;
            _subscribedCharacteristic = null;
        }
    }
}
