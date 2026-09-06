using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.App.Services;
using GeroImperium.Core.Ble;
using GeroImperium.Core.Data;
using GeroImperium.Core.Http;
using GeroImperium.Core.Imaging;
using GeroImperium.Core.Models;
using Microsoft.Win32;

namespace GeroImperium.App.ViewModels;

/// <summary>
/// Diff-and-push sync straight against the device's REST API (doc/plan2.md's sync model) -- no more Sync
/// Service/IPC involvement for CRUD, that pipe is now provisioning-handoff + App-Launch events only (see
/// Core/Ipc's shrunk surface, doc/plan2.md phase 12.5). Walks ApplicationPages -> Applications [+images] ->
/// KeyGroups -> KeyActions -> GeroImperiumKeys [+images] in FK order; POSTs a row with no RemoteId yet, PUTs
/// one that's dirty, skips one that's neither (doc/plan2.md's "skip if unchanged" open decision -- resolved by
/// a Dirty column per row plus an image-specific timestamp comparison, see GeroImperiumRepository's doc
/// comment). Locally-deleted rows that had already been pushed are tracked as PendingDeletes tombstones and
/// DELETEd first, before any create/update (the "tombstone tracking for deletes" open decision).
///
/// Also exposes two more destructive, explicitly-confirmed operations: Full Sync (wipes every row on the
/// device, then pushes a clean copy of the local cache) and Pull from Device (backs up the local cache, then
/// replaces it entirely with what's on the device) -- doc/plan2.md's "full device -> local pull" open
/// decision, resolved as a real flow rather than left unaddressed.
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
    public IAsyncRelayCommand FullSyncCommand { get; }
    public IAsyncRelayCommand PullFromDeviceCommand { get; }
    public IRelayCommand BackupNowCommand { get; }
    public IRelayCommand RestoreBackupCommand { get; }

    public SyncViewModel(GeroImperiumRepository repository)
    {
        _repository = repository;

        // Commands must exist before DeviceIpAddress is set below -- OnDeviceIpAddressChanged fires the
        // instant the setter sees a value different from the field's "" default (i.e. whenever a device IP
        // was already persisted from a prior Connect or from provisioning) and dereferences ConnectCommand.
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy && DeviceIpAddress.Trim().Length > 0);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected && !IsBusy);
        SyncCommand = new AsyncRelayCommand(SyncAsync, () => IsConnected && !IsBusy);
        FullSyncCommand = new AsyncRelayCommand(FullSyncAsync, () => IsConnected && !IsBusy);
        PullFromDeviceCommand = new AsyncRelayCommand(PullFromDeviceAsync, () => IsConnected && !IsBusy);
        BackupNowCommand = new RelayCommand(BackupNow, () => !IsBusy);
        RestoreBackupCommand = new RelayCommand(RestoreBackup, () => !IsBusy);

        DeviceIpAddress = _repository.GetGeneralSettings().DeviceIpAddress ?? string.Empty;

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
        if (_client is null)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        try
        {
            await PushAllAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Wipes every row on the device (cascading DELETEs from the top-level tables, plus KeyActions
    /// separately since it isn't FK'd under a page), resets every local RemoteId/Dirty so the next push treats
    /// everything as brand-new, then runs a normal push. Explicitly confirmed first -- this cannot be undone
    /// on the device.</summary>
    private async Task FullSyncAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            "This DELETES every Application, Key Group, Key, and Shortcut currently on the device, then pushes " +
            "a fresh copy from this computer's local editor. This cannot be undone on the device.\n\nContinue?",
            "Full Sync", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        try
        {
            StatusText = "Wiping device data...";

            // The device cascades a page's DELETE down to its applications/key groups/keys itself
            // (doc/windows_app_api_guide.md), so deleting every page clears almost everything in one pass.
            List<ApplicationPageRow> remotePages = await client.GetAllAsync<ApplicationPageRow>(GeroImperiumTables.ApplicationPages);
            foreach (var page in remotePages)
            {
                await client.DeleteAsync(GeroImperiumTables.ApplicationPages, page.Id);
            }

            // KeyActions aren't FK'd under a page (a key references one, not the reverse), so pages cascading
            // away doesn't touch this table -- clear it explicitly for a genuinely clean wipe.
            List<KeyActionRow> remoteActions = await client.GetAllAsync<KeyActionRow>(GeroImperiumTables.KeyActions);
            foreach (var action in remoteActions)
            {
                await client.DeleteAsync(GeroImperiumTables.KeyActions, action.Id);
            }

            _repository.ClearAllRemoteIds();
            StatusText = "Device wiped -- pushing a fresh copy...";

            await PushAllAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Full Sync failed while wiping the device: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The shared push core used by both a normal Sync and the second half of a Full Sync. Never
    /// throws -- every failure is caught and reported via StatusText, matching the existing Sync UX (a caller
    /// invoking this back-to-back with a wipe, e.g. FullSyncAsync, doesn't need its own try/catch around it).</summary>
    private async Task PushAllAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        try
        {
            List<PendingDelete> pendingDeletes = _repository.GetPendingDeletes();
            List<ApplicationPage> pages = _repository.GetApplicationPages();
            List<Core.Models.Application> applications = _repository.GetApplications();
            List<KeyAction> keyActions = _repository.GetKeyActions();
            var groupsByApp = applications.ToDictionary(a => a.Id, a => _repository.GetKeyGroups(a.Id));
            var keysByGroup = groupsByApp.Values.SelectMany(g => g).ToDictionary(g => g.Id, g => _repository.GetKeys(g.Id));

            // Only ActionType.Shortcut has any device-side meaning today (firmware only executes shortcuts,
            // per pc_app_plan.md's "Action types and the firmware gap") -- LaunchApp/Script rows are pushed
            // to no table and stay RemoteId == null; Keys referencing them fall back to KeyActionId = null.
            var syncableActions = keyActions.Where(a => a.ActionType == ActionType.Shortcut).ToList();

            int total = pendingDeletes.Count + pages.Count + applications.Count + syncableActions.Count
                + groupsByApp.Values.Sum(g => g.Count) + keysByGroup.Values.Sum(k => k.Count);
            int done = 0;

            // Tracks whether anything was actually written to the device (as opposed to every row being
            // skipped as unchanged) -- only worth rebooting the device below if something changed.
            bool changesPushed = false;

            void Advance(string stage)
            {
                done++;
                ProgressPercent = total == 0 ? 100 : 100.0 * done / total;
                StatusText = stage;
            }

            // Tracks what was actually in flight when a call throws -- the catch below reports this instead of
            // just the raw exception text, since "Synced X" (the last Advance() call) can be several rows
            // behind the one that failed (e.g. its row PUT succeeded but its image POST didn't).
            string currentStage = "reading the local authoring database";
            try
            {
                foreach (var tombstone in pendingDeletes)
                {
                    currentStage = $"deleting {tombstone.TableName} #{tombstone.RemoteId} on the device";
                    await client.DeleteAsync(tombstone.TableName, tombstone.RemoteId);
                    _repository.RemovePendingDelete(tombstone.Id);
                    changesPushed = true;
                    Advance($"Deleted {tombstone.TableName} #{tombstone.RemoteId} from the device.");
                }

                foreach (var page in pages)
                {
                    if (page.RemoteId is null || page.Dirty)
                    {
                        currentStage = $"page '{page.Name}' (row)";
                        long remoteId = await CreateOrUpdateAsync<ApplicationPageRow>(client, GeroImperiumTables.ApplicationPages,
                            page.RemoteId, new { page.Order, page.Name }, r => r.Id);
                        page.RemoteId = remoteId;
                        _repository.MarkApplicationPageSynced(page.Id, remoteId);
                        changesPushed = true;
                        Advance($"Synced page '{page.Name}'.");
                    }
                    else
                    {
                        Advance($"Skipped page '{page.Name}' (unchanged).");
                    }
                }

                foreach (var app in applications)
                {
                    long pageRemoteId = pages.First(p => p.Id == app.ApplicationPageId).RemoteId
                        ?? throw new InvalidOperationException($"Page {app.ApplicationPageId} has no RemoteId after syncing.");

                    bool needsRowPush = app.RemoteId is null || app.Dirty;
                    if (needsRowPush)
                    {
                        currentStage = $"application '{app.Name}' (row)";
                        app.RemoteId = await CreateOrUpdateAsync<ApplicationRow>(client, GeroImperiumTables.Applications,
                            app.RemoteId, new { ApplicationPageId = pageRemoteId, app.Order, app.Name }, r => r.Id);
                    }

                    bool needsImagePush = app.ImageDataRgb565 is not null && app.ImageChangedAtUtc != app.LastSyncedImageChangedAtUtc;
                    if (needsImagePush)
                    {
                        currentStage = $"application '{app.Name}' (image, {app.ImageDataRgb565!.Length} bytes)";
                        await client.UploadImageAsync(GeroImperiumTables.Applications, app.RemoteId!.Value, app.ImageDataRgb565);
                    }

                    if (needsRowPush || needsImagePush)
                    {
                        _repository.MarkApplicationSynced(app.Id, app.RemoteId!.Value,
                            needsImagePush ? app.ImageChangedAtUtc : app.LastSyncedImageChangedAtUtc);
                        changesPushed = true;
                        Advance($"Synced application '{app.Name}'.");
                    }
                    else
                    {
                        Advance($"Skipped application '{app.Name}' (unchanged).");
                    }
                }

                foreach (var app in applications)
                {
                    foreach (var group in groupsByApp[app.Id])
                    {
                        if (group.RemoteId is null || group.Dirty)
                        {
                            currentStage = $"key group '{group.Name}' (row)";
                            long remoteId = await CreateOrUpdateAsync<KeyGroupRow>(client, GeroImperiumTables.KeyGroups,
                                group.RemoteId, new { ApplicationId = app.RemoteId!.Value, group.Order, group.Name }, r => r.Id);
                            group.RemoteId = remoteId;
                            _repository.MarkKeyGroupSynced(group.Id, remoteId);
                            changesPushed = true;
                            Advance($"Synced key group '{group.Name}'.");
                        }
                        else
                        {
                            Advance($"Skipped key group '{group.Name}' (unchanged).");
                        }
                    }
                }

                foreach (var action in syncableActions)
                {
                    if (action.RemoteId is null || action.Dirty)
                    {
                        currentStage = $"key action {action.Id} (row)";
                        long remoteId = await CreateOrUpdateAsync<KeyActionRow>(client, GeroImperiumTables.KeyActions,
                            action.RemoteId, new { Type = (int)action.Type, action.TextContent }, r => r.Id);
                        action.RemoteId = remoteId;
                        _repository.MarkKeyActionSynced(action.Id, remoteId);
                        changesPushed = true;
                        Advance("Synced a key action.");
                    }
                    else
                    {
                        Advance("Skipped a key action (unchanged).");
                    }
                }

                foreach (var group in groupsByApp.Values.SelectMany(g => g))
                {
                    foreach (var key in keysByGroup[group.Id])
                    {
                        long? actionRemoteId = key.KeyActionId is long localActionId
                            ? syncableActions.FirstOrDefault(a => a.Id == localActionId)?.RemoteId
                            : null;

                        bool needsRowPush = key.RemoteId is null || key.Dirty;
                        if (needsRowPush)
                        {
                            currentStage = $"key {key.Position} in '{group.Name}' (row)";
                            key.RemoteId = await CreateOrUpdateAsync<KeyRow>(client, GeroImperiumTables.Keys,
                                key.RemoteId, new { KeyGroupId = group.RemoteId!.Value, key.Position, KeyActionId = actionRemoteId }, r => r.Id);
                        }

                        bool needsImagePush = key.ImageDataRgb565 is not null && key.ImageChangedAtUtc != key.LastSyncedImageChangedAtUtc;
                        if (needsImagePush)
                        {
                            currentStage = $"key {key.Position} in '{group.Name}' (image, {key.ImageDataRgb565!.Length} bytes)";
                            await client.UploadImageAsync(GeroImperiumTables.Keys, key.RemoteId!.Value, key.ImageDataRgb565);
                        }

                        if (needsRowPush || needsImagePush)
                        {
                            _repository.MarkKeySynced(key.Id, key.RemoteId!.Value,
                                needsImagePush ? key.ImageChangedAtUtc : key.LastSyncedImageChangedAtUtc);
                            changesPushed = true;
                            Advance($"Synced key {key.Position} in '{group.Name}'.");
                        }
                        else
                        {
                            Advance($"Skipped key {key.Position} in '{group.Name}' (unchanged).");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"while syncing {currentStage}: {ex.Message}", ex);
            }

            string summary = $"Sync complete -- {done} row(s) processed.";
            if (changesPushed)
            {
                summary += " " + await RestartDeviceAsync(client);
            }

            StatusText = summary;
        }
        catch (Exception ex)
        {
            StatusText = $"Sync failed: {ex.Message}";
        }
    }

    /// <summary>Tells the device to reboot so it picks up whatever was just pushed (POST api/restart, no body).
    /// A connection drop/timeout right after issuing this is the expected shape of a device that's already
    /// rebooting, not a real failure -- only an actual HTTP error response (the endpoint rejecting the request
    /// outright) is reported as a problem. Either way, stops REST polling and flips the connection indicator
    /// immediately rather than letting RestPollLoopAsync stumble into the device going down mid-reboot and
    /// report it as a scary "Lost connection".</summary>
    private async Task<string> RestartDeviceAsync(GeroImperiumClient client)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.RestartAsync(cts.Token);
        }
        catch (GeroImperiumApiException ex)
        {
            return $"Could not restart the device ({ex.Message}) -- restart it manually if needed.";
        }
        catch (Exception)
        {
            // Timeout/connection-reset -- treat as the device having already gone down to reboot.
        }

        StopPolling();
        _client?.Dispose();
        _client = null;
        IsConnected = false;
        ConnectionStatus = "Device restarting -- reconnect once it's back online.";
        return "Restarting the device to apply changes.";
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

    /// <summary>Backs up the local authoring cache first (always -- this is destructive to everything not yet
    /// pushed), clears it, then re-imports every row (and image) currently on the device. Adopts a
    /// device that already has data configured on it -- e.g. reinstalling this app, or setting up a second PC
    /// -- at the cost of discarding any local-only edits that were never synced (hence the backup).</summary>
    private async Task PullFromDeviceAsync()
    {
        if (_client is not { } client)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            "This REPLACES everything in this computer's local editor with a fresh copy pulled from the device. " +
            "Your current local data will be backed up first and can be restored afterward (Restore Backup), " +
            "but any local edits never pushed to the device will otherwise be lost.\n\nContinue?",
            "Pull from Device", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        string backupPath = AppPaths.NewBackupFilePath("before-pull");
        try
        {
            StatusText = "Backing up local data...";
            _repository.Database.BackupTo(backupPath);

            _repository.ClearAllSyncedDataForPull();

            StatusText = "Reading data from the device...";
            List<ApplicationPageRow> remotePages = await client.GetAllAsync<ApplicationPageRow>(GeroImperiumTables.ApplicationPages);
            List<ApplicationRow> remoteApps = await client.GetAllAsync<ApplicationRow>(GeroImperiumTables.Applications);
            List<KeyGroupRow> remoteGroups = await client.GetAllAsync<KeyGroupRow>(GeroImperiumTables.KeyGroups);
            List<KeyActionRow> remoteActions = await client.GetAllAsync<KeyActionRow>(GeroImperiumTables.KeyActions);
            List<KeyRow> remoteKeys = await client.GetAllAsync<KeyRow>(GeroImperiumTables.Keys);

            int total = remotePages.Count + remoteApps.Count + remoteGroups.Count + remoteActions.Count + remoteKeys.Count;
            int done = 0;
            void Advance(string stage)
            {
                done++;
                ProgressPercent = total == 0 ? 100 : 100.0 * done / total;
                StatusText = stage;
            }

            var pageIdMap = new Dictionary<long, long>();
            foreach (var page in remotePages)
            {
                pageIdMap[page.Id] = _repository.InsertPulledApplicationPage(page.Id, page.Order, page.Name);
                Advance($"Pulled page '{page.Name}'.");
            }

            var appIdMap = new Dictionary<long, long>();
            foreach (var app in remoteApps)
            {
                if (!pageIdMap.TryGetValue(app.ApplicationPageId, out var localPageId))
                {
                    Advance($"Skipped application '{app.Name}' (its page wasn't pulled).");
                    continue;
                }

                var (previewPng, rgb565, changedAt) = await DownloadPulledImageAsync(client, GeroImperiumTables.Applications, app.Id);
                appIdMap[app.Id] = _repository.InsertPulledApplication(app.Id, localPageId, app.Order, app.Name, previewPng, rgb565, changedAt);
                Advance($"Pulled application '{app.Name}'.");
            }

            var groupIdMap = new Dictionary<long, long>();
            foreach (var group in remoteGroups)
            {
                if (!appIdMap.TryGetValue(group.ApplicationId, out var localAppId))
                {
                    Advance($"Skipped key group '{group.Name}' (its application wasn't pulled).");
                    continue;
                }

                groupIdMap[group.Id] = _repository.InsertPulledKeyGroup(group.Id, localAppId, group.Order, group.Name);
                Advance($"Pulled key group '{group.Name}'.");
            }

            var actionIdMap = new Dictionary<long, long>();
            foreach (var action in remoteActions)
            {
                actionIdMap[action.Id] = _repository.InsertPulledKeyAction(action.Id, (HidActionKind)action.Type, action.TextContent);
                Advance("Pulled a key action.");
            }

            foreach (var key in remoteKeys)
            {
                if (!groupIdMap.TryGetValue(key.KeyGroupId, out var localGroupId))
                {
                    Advance($"Skipped key {key.Position} (its key group wasn't pulled).");
                    continue;
                }

                long? localActionId = key.KeyActionId is long remoteActionId && actionIdMap.TryGetValue(remoteActionId, out var mappedId)
                    ? mappedId
                    : null;

                var (previewPng, rgb565, changedAt) = await DownloadPulledImageAsync(client, GeroImperiumTables.Keys, key.Id);
                _repository.InsertPulledKey(key.Id, localGroupId, key.Position, localActionId, previewPng, rgb565, changedAt);
                Advance($"Pulled key {key.Position}.");
            }

            StatusText = $"Pull complete -- {done} row(s) imported. Local backup saved to '{Path.GetFileName(backupPath)}'. " +
                "Restart the app to see the pulled data in the Applications/Keys pages.";
        }
        catch (Exception ex)
        {
            StatusText = $"Pull from Device failed: {ex.Message}. Your local data was already backed up to " +
                $"'{Path.GetFileName(backupPath)}' before this started -- use Restore Backup to get it back.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads a row's image (if it has one) and reconstructs a displayable preview PNG from the
    /// device-format bytes -- there's no original source image to fall back on for a pulled row.</summary>
    private static async Task<(byte[]? PreviewPng, byte[]? Rgb565, DateTime? ChangedAtUtc)> DownloadPulledImageAsync(
        GeroImperiumClient client, string table, long remoteId)
    {
        byte[]? rgb565 = await client.DownloadImageAsync(table, remoteId);
        if (rgb565 is null)
        {
            return (null, null, null);
        }

        byte[] previewPng = ImagePipeline.ConvertRgb565ToPreviewPng(rgb565);
        return (previewPng, rgb565, DateTime.UtcNow);
    }

    /// <summary>Manual, user-chosen-destination backup -- separate from Pull from Device's automatic backup
    /// (which always writes to AppPaths.BackupDirectory). Purely local -- no device connection needed, so
    /// this command is enabled independent of IsConnected.</summary>
    private void BackupNow()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Backup local database to...",
            InitialDirectory = Directory.Exists(AppPaths.BackupDirectory) ? AppPaths.BackupDirectory : null,
            FileName = Path.GetFileName(AppPaths.NewBackupFilePath("manual")),
            Filter = "GeroImperium backups (*.db)|*.db|All files (*.*)|*.*",
            DefaultExt = ".db",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _repository.Database.BackupTo(dialog.FileName);
            StatusText = $"Backed up local data to '{dialog.FileName}'.";
        }
        catch (Exception ex)
        {
            StatusText = $"Backup failed: {ex.Message}";
        }
    }

    /// <summary>Lets the user pick one of AppPaths.BackupDirectory's timestamped backups (auto-taken before
    /// every Pull) and restore it live over the running database. Purely local -- no device connection
    /// needed, so this command is enabled independent of IsConnected.</summary>
    private void RestoreBackup()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Restore a backup",
            InitialDirectory = Directory.Exists(AppPaths.BackupDirectory) ? AppPaths.BackupDirectory : null,
            Filter = "GeroImperium backups (*.db)|*.db|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"This REPLACES everything in this computer's local editor with the contents of:\n{dialog.FileName}\n\n" +
            "The app must be restarted afterward to load the restored data. Continue?",
            "Restore Backup", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        try
        {
            _repository.Database.RestoreFrom(dialog.FileName);
            StatusText = "Backup restored -- restart the app to see the restored data.";
            MessageBox.Show("Restore complete. Please restart the app now to load the restored data.",
                "Restore Backup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
    }

    private void NotifyAllCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        SyncCommand.NotifyCanExecuteChanged();
        FullSyncCommand.NotifyCanExecuteChanged();
        PullFromDeviceCommand.NotifyCanExecuteChanged();
        BackupNowCommand.NotifyCanExecuteChanged();
        RestoreBackupCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _lifetimeCts.Cancel();
        StopPolling();
        _client?.Dispose();
    }
}
