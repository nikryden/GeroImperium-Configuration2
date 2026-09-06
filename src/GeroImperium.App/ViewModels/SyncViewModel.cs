using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Ble;
using GeroImperium.Core.Data;
using GeroImperium.Core.Http;
using GeroImperium.Core.Models;

namespace GeroImperium.App.ViewModels;

/// <summary>
/// Diff-and-push sync straight against the device's REST API (doc/plan2.md's sync model) -- no more Sync
/// Service/IPC involvement for CRUD, that pipe is now provisioning-handoff + App-Launch events only (see
/// Core/Ipc's shrunk surface, doc/plan2.md phase 12.5). Walks ApplicationPages -> Applications [+images] ->
/// KeyGroups -> KeyActions -> GeroImperiumKeys [+images] in FK order; POSTs a row with no RemoteId yet, PUTs
/// one that already has one (unconditionally -- "skip if unchanged" needs a last-synced-snapshot this app
/// doesn't track yet, so every Sync click currently re-sends every already-pushed row; correct, just not
/// bandwidth-optimal). Deletes are NOT sent -- tombstone tracking for locally-removed rows is still an open
/// decision in doc/plan2.md's "Open decisions", so today's Sync only ever creates/updates.
/// </summary>
public sealed partial class SyncViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RestPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BlePresenceInterval = TimeSpan.FromSeconds(20);

    private readonly GeroImperiumRepository _repository;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private GeroImperiumClient? _client;
    private CancellationTokenSource? _pollCts;

    [ObservableProperty]
    private string _deviceIpAddress = string.Empty;

    [ObservableProperty]
    private string _connectionStatus = "Not connected";

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>Cheap "is the device paired and nearby over Bluetooth" signal (doc/plan2.md's "Device
    /// discovery & the 'connected' indicator") -- independent of REST reachability, refreshed continuously
    /// regardless of Connect/Disconnect state. Useful to tell "device is off WiFi" apart from "device is off"
    /// once a Reconnect-via-Bluetooth flow exists (phase 12.7); today it's just displayed.</summary>
    [ObservableProperty]
    private bool _isBlePaired;

    public string BleStatusText => IsBlePaired ? "Bluetooth: device paired and nearby" : "Bluetooth: not detected";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Unlike the old CDC bulk push, a dropped connection mid-sync isn't catastrophic -- every row is
    /// pushed individually and is idempotent to retry (doc/plan2.md) -- so this only guards against editing
    /// the row currently in flight, not a device-safety concern.</summary>
    public bool CanNavigateAway => !IsBusy;

    public IAsyncRelayCommand ConnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand SyncCommand { get; }

    public SyncViewModel(GeroImperiumRepository repository)
    {
        _repository = repository;
        DeviceIpAddress = _repository.GetGeneralSettings().DeviceIpAddress ?? string.Empty;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy && DeviceIpAddress.Trim().Length > 0);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected && !IsBusy);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => IsConnected && !IsBusy);

        _ = BlePresenceLoopAsync(_lifetimeCts.Token);
    }

    partial void OnIsConnectedChanged(bool value) => NotifyAllCommands();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanNavigateAway));
        NotifyAllCommands();
    }

    partial void OnDeviceIpAddressChanged(string value) => ConnectCommand.NotifyCanExecuteChanged();

    partial void OnIsBlePairedChanged(bool value) => OnPropertyChanged(nameof(BleStatusText));

    private async Task ConnectAsync()
    {
        var ip = DeviceIpAddress.Trim();
        if (ip.Length == 0)
        {
            ConnectionStatus = "Enter the device's IP address first.";
            return;
        }

        ConnectionStatus = "Connecting...";
        var client = new GeroImperiumClient(ip);
        try
        {
            DeviceStatus status = await client.GetStatusAsync();
            _client = client;
            IsConnected = true;
            ApplyStatus(status);
            StartPolling();

            var settings = _repository.GetGeneralSettings();
            if (settings.DeviceIpAddress != ip)
            {
                settings.DeviceIpAddress = ip;
                _repository.UpdateGeneralSettings(settings);
            }
        }
        catch (Exception ex)
        {
            client.Dispose();
            ConnectionStatus = $"Connection failed: {ex.Message}";
        }
    }

    private void Disconnect()
    {
        StopPolling();
        _client?.Dispose();
        _client = null;
        IsConnected = false;
        ConnectionStatus = "Not connected";
    }

    private void ApplyStatus(DeviceStatus status)
    {
        var wifi = status.Wifi;
        ConnectionStatus = wifi is null
            ? $"Connected to {status.Device}."
            : $"Connected to {status.Device} -- WiFi {wifi.State}, {wifi.Ip}, RSSI {wifi.Rssi} dBm.";
    }

    private void StartPolling()
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        _ = RestPollLoopAsync(_pollCts.Token);
    }

    private void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    /// <summary>Keeps ConnectionStatus fresh (WiFi state/IP/RSSI can change) and detects a dropped connection
    /// while idle, rather than only ever finding out on the next user-initiated action (doc/plan2.md's
    /// "Device discovery & the 'connected' indicator"). Skips a tick while a Sync is in flight -- polling
    /// would just queue behind it on GeroImperiumClient's single-flight semaphore anyway, no reason to stack
    /// up waiters.</summary>
    private async Task RestPollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(RestPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
            {
                if (_client is not { } client || IsBusy)
                {
                    continue;
                }

                try
                {
                    DeviceStatus status = await client.GetStatusAsync(ct);
                    ApplyStatus(status);
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    ConnectionStatus = $"Lost connection: {ex.Message}";
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Independent of Connect/Disconnect -- runs for the lifetime of this ViewModel so the indicator
    /// reflects Bluetooth pairing state even before the first REST connect.</summary>
    private async Task BlePresenceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(BlePresenceInterval);
        try
        {
            while (true)
            {
                try
                {
                    IsBlePaired = await DeviceBleClient.IsPairedAsync(ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Best-effort presence signal -- a transient WinRT failure shouldn't stop future checks.
                }

                if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(true))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SyncAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        try
        {
            List<ApplicationPage> pages = _repository.GetApplicationPages();
            List<Core.Models.Application> applications = _repository.GetApplications();
            List<KeyAction> keyActions = _repository.GetKeyActions();
            var groupsByApp = applications.ToDictionary(a => a.Id, a => _repository.GetKeyGroups(a.Id));
            var keysByGroup = groupsByApp.Values.SelectMany(g => g).ToDictionary(g => g.Id, g => _repository.GetKeys(g.Id));

            // Only ActionType.Shortcut has any device-side meaning today (firmware only executes shortcuts,
            // per pc_app_plan.md's "Action types and the firmware gap") -- LaunchApp/Script rows are pushed
            // to no table and stay RemoteId == null; Keys referencing them fall back to KeyActionId = null.
            var syncableActions = keyActions.Where(a => a.ActionType == ActionType.Shortcut).ToList();

            int total = pages.Count + applications.Count + syncableActions.Count
                + groupsByApp.Values.Sum(g => g.Count) + keysByGroup.Values.Sum(k => k.Count);
            int done = 0;
            void Advance(string stage)
            {
                done++;
                ProgressPercent = total == 0 ? 100 : 100.0 * done / total;
                StatusText = stage;
            }

            foreach (var page in pages)
            {
                page.RemoteId = await CreateOrUpdateAsync<ApplicationPageRow>(client, GeroImperiumTables.ApplicationPages,
                    page.RemoteId, new { page.Order, page.Name }, r => r.Id);
                _repository.UpdateApplicationPage(page);
                Advance($"Synced page '{page.Name}'.");
            }

            foreach (var app in applications)
            {
                long pageRemoteId = pages.First(p => p.Id == app.ApplicationPageId).RemoteId
                    ?? throw new InvalidOperationException($"Page {app.ApplicationPageId} has no RemoteId after syncing.");

                app.RemoteId = await CreateOrUpdateAsync<ApplicationRow>(client, GeroImperiumTables.Applications,
                    app.RemoteId, new { ApplicationPageId = pageRemoteId, app.Order, app.Name }, r => r.Id);
                _repository.UpdateApplication(app);

                if (app.ImageDataRgb565 is { } appImage)
                {
                    await client.UploadImageAsync(GeroImperiumTables.Applications, app.RemoteId.Value, appImage);
                }

                Advance($"Synced application '{app.Name}'.");
            }

            foreach (var app in applications)
            {
                foreach (var group in groupsByApp[app.Id])
                {
                    group.RemoteId = await CreateOrUpdateAsync<KeyGroupRow>(client, GeroImperiumTables.KeyGroups,
                        group.RemoteId, new { ApplicationId = app.RemoteId!.Value, group.Order, group.Name }, r => r.Id);
                    _repository.UpdateKeyGroup(group);
                    Advance($"Synced key group '{group.Name}'.");
                }
            }

            foreach (var action in syncableActions)
            {
                action.RemoteId = await CreateOrUpdateAsync<KeyActionRow>(client, GeroImperiumTables.KeyActions,
                    action.RemoteId, new { Type = (int)action.Type, action.TextContent }, r => r.Id);
                _repository.UpdateKeyActionRemoteId(action);
                Advance("Synced a key action.");
            }

            foreach (var group in groupsByApp.Values.SelectMany(g => g))
            {
                foreach (var key in keysByGroup[group.Id])
                {
                    long? actionRemoteId = key.KeyActionId is long localActionId
                        ? syncableActions.FirstOrDefault(a => a.Id == localActionId)?.RemoteId
                        : null;

                    key.RemoteId = await CreateOrUpdateAsync<KeyRow>(client, GeroImperiumTables.Keys,
                        key.RemoteId, new { KeyGroupId = group.RemoteId!.Value, key.Position, KeyActionId = actionRemoteId }, r => r.Id);
                    _repository.UpdateKey(key);

                    if (key.ImageDataRgb565 is { } keyImage)
                    {
                        await client.UploadImageAsync(GeroImperiumTables.Keys, key.RemoteId.Value, keyImage);
                    }

                    Advance($"Synced key {key.Position} in '{group.Name}'.");
                }
            }

            StatusText = $"Sync complete -- {done} row(s) pushed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>PUTs (and returns the same id) if remoteId is already set, else POSTs and returns the newly
    /// created row's Id -- the one decision point that makes this "diff-and-push" rather than "always create".</summary>
    private static async Task<long> CreateOrUpdateAsync<TRow>(GeroImperiumClient client, string table, long? remoteId,
        object body, Func<TRow, long> getId)
    {
        if (remoteId is long id)
        {
            await client.UpdateAsync(table, id, body);
            return id;
        }

        TRow created = await client.CreateAsync<TRow>(table, body);
        return getId(created);
    }

    private void NotifyAllCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SyncCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        StopPolling();
        _client?.Dispose();
    }
}
