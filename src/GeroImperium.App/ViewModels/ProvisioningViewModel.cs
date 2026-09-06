using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Ble;
using GeroImperium.Core.Data;
using GeroImperium.Core.Ipc;

namespace GeroImperium.App.ViewModels;

/// <summary>
/// First-run BLE WiFi provisioning (doc/plan2.md phase 12.7). Borrows the device's one BLE connection slot from
/// the Sync Service via IPC (RequestBleForProvisioningAsync/ReleaseBleAfterProvisioningAsync -- NimBLE allows
/// exactly one concurrent GATT connection, see doc/plan2.md's "Why the Service still exists"), drives
/// WifiProvisioningService directly in this process against the freed slot, then always hands the slot back to
/// the Service (try/finally) so its App-Launch subscription resumes regardless of outcome. On success, persists
/// the learned IP to GeneralSettings and raises IpLearned so SyncViewModel picks it up without an app restart.
/// </summary>
public sealed partial class ProvisioningViewModel : ObservableObject
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly GeroImperiumRepository _repository;

    [ObservableProperty]
    private string _ssid = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText =
        "Hold the device's pairing button for 10s to put it in pairing mode, enter its WiFi credentials, then press Provision.";

    public IAsyncRelayCommand ProvisionCommand { get; }

    /// <summary>Fired once the device reports Connected and its IP is persisted, so MainViewModel can push the
    /// new address straight into SyncViewModel without requiring the user to re-enter it.</summary>
    public event EventHandler<string>? IpLearned;

    public ProvisioningViewModel(GeroImperiumRepository repository)
    {
        _repository = repository;
        ProvisionCommand = new AsyncRelayCommand(ProvisionAsync, () => !IsBusy && Ssid.Trim().Length > 0);
    }

    partial void OnIsBusyChanged(bool value) => ProvisionCommand.NotifyCanExecuteChanged();

    partial void OnSsidChanged(string value) => ProvisionCommand.NotifyCanExecuteChanged();

    private async Task ProvisionAsync()
    {
        var ssid = Ssid.Trim();
        var password = Password;

        IsBusy = true;
        using var ipc = new IpcDeviceClient();
        bool bleRequested = false;
        try
        {
            StatusText = "Connecting to the Sync Service...";
            if (!await ipc.ConnectAsync())
            {
                StatusText = "Sync Service isn't running -- start it (tray icon) and try again.";
                return;
            }

            StatusText = "Requesting the Bluetooth connection from the Sync Service...";
            IpcResponse handoff = await ipc.RequestBleForProvisioningAsync();
            if (handoff.Kind == IpcResponseKind.Error)
            {
                StatusText = $"Sync Service refused the Bluetooth handoff: {handoff.ErrorMessage}";
                return;
            }

            bleRequested = true;

            StatusText = "Looking for the device over Bluetooth...";
            using var bleClient = new DeviceBleClient();
            if (!await bleClient.ConnectAsync())
            {
                StatusText = "Couldn't find the device over Bluetooth -- make sure it's nearby and in pairing mode.";
                return;
            }

            StatusText = "Sending WiFi credentials...";
            var provisioning = new WifiProvisioningService(bleClient);
            if (!await provisioning.ProvisionAsync(ssid, password))
            {
                StatusText = "Failed to send WiFi credentials to the device.";
                return;
            }

            StatusText = "Waiting for the device to connect...";
            WifiStatus? status = await provisioning.PollUntilConnectedAsync(ConnectTimeout);
            if (status is not { IsConnected: true })
            {
                StatusText = status is null
                    ? "Lost contact with the device while waiting for it to connect."
                    : $"Device did not connect within {ConnectTimeout.TotalSeconds:0}s (last state: {status.State}).";
                return;
            }

            var settings = _repository.GetGeneralSettings();
            settings.DeviceIpAddress = status.IpAddress;
            _repository.UpdateGeneralSettings(settings);
            IpLearned?.Invoke(this, status.IpAddress);

            StatusText = $"Connected -- device is now reachable at {status.IpAddress}. Go to Sync to push your data.";
        }
        catch (Exception ex)
        {
            StatusText = $"Provisioning failed: {ex.Message}";
        }
        finally
        {
            if (bleRequested)
            {
                try
                {
                    await ipc.ReleaseBleAfterProvisioningAsync();
                }
                catch (Exception)
                {
                    // Best-effort -- if the pipe is already gone there's nothing more to release from here; the
                    // Service only ever pauses its reconnect loop on this same explicit request, so worst case
                    // it stays paused until the Service is restarted rather than corrupting any shared state.
                }
            }

            IsBusy = false;
        }
    }
}
