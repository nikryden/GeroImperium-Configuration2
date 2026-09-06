using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Data;
using GeroImperium.Core.Http;
using GeroImperium.Core.Ipc;
using GeroImperium.Core.Models;
using CoreApplication = GeroImperium.Core.Models.Application;

namespace GeroImperium.App.ViewModels;

/// <summary>
/// Push to device. Bulk S/D/F for first-push/full-profile-restore; incremental I/J/T for single-field
/// pushes -- matching the "which path to use" table in pc_app_integration.md. Talks to the Sync Service over
/// a named pipe (Core/Ipc) rather than opening the CDC port itself -- the Service is the single owner of the
/// port (see pc_app_plan.md "Both App and Service open the CDC port...").
/// </summary>
public sealed partial class SyncViewModel : ObservableObject, IDisposable
{
    private readonly GeroImperiumRepository _repository;
    private readonly string _databasePath;
    private readonly IpcDeviceClient _ipcClient = new();

    [ObservableProperty]
    private string _connectionStatus = "Not connected";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>False while a bulk upload is in progress -- surfaced so the shell can disable navigation.
    /// A dropped mid-bulk connection has no recovery path on the device (pc_app_integration.md), so the
    /// user must not be able to wander off mid-upload.</summary>
    public bool CanNavigateAway => !IsBusy;

    public ObservableCollection<CoreApplication> Applications { get; } = [];

    [ObservableProperty]
    private CoreApplication? _selectedApplication;

    public ObservableCollection<KeyGroup> KeyGroups { get; } = [];

    [ObservableProperty]
    private KeyGroup? _selectedKeyGroup;

    public ObservableCollection<GeroImperiumKey> Keys { get; } = [];

    [ObservableProperty]
    private GeroImperiumKey? _selectedKey;

    public IAsyncRelayCommand ConnectCommand { get; }
    public IRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand PushFullDatabaseCommand { get; }
    public IAsyncRelayCommand PushApplicationImageCommand { get; }
    public IAsyncRelayCommand PushKeyImageCommand { get; }
    public IAsyncRelayCommand PushKeyShortcutCommand { get; }

    public SyncViewModel(GeroImperiumRepository repository, string databasePath)
    {
        _repository = repository;
        _databasePath = databasePath;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected && !IsBusy);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected && !IsBusy);
        PushFullDatabaseCommand = new AsyncRelayCommand(PushFullDatabaseAsync, () => IsConnected && !IsBusy);
        PushApplicationImageCommand = new AsyncRelayCommand(PushApplicationImageAsync, () => IsConnected && !IsBusy && SelectedApplication is not null);
        PushKeyImageCommand = new AsyncRelayCommand(PushKeyImageAsync, () => IsConnected && !IsBusy && SelectedKeyGroup is not null && SelectedKey is not null);
        PushKeyShortcutCommand = new AsyncRelayCommand(PushKeyShortcutAsync, () => IsConnected && !IsBusy && SelectedKeyGroup is not null && SelectedKey is not null);

        foreach (var app in _repository.GetApplications())
        {
            Applications.Add(app);
        }
    }

    partial void OnIsConnectedChanged(bool value) => NotifyAllCommands();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanNavigateAway));
        NotifyAllCommands();
    }

    partial void OnSelectedApplicationChanged(CoreApplication? value)
    {
        KeyGroups.Clear();
        SelectedKeyGroup = null;

        if (value is not null)
        {
            foreach (var group in _repository.GetKeyGroups(value.Id))
            {
                KeyGroups.Add(group);
            }
        }

        PushApplicationImageCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedKeyGroupChanged(KeyGroup? value)
    {
        Keys.Clear();
        SelectedKey = null;

        if (value is not null)
        {
            foreach (var key in _repository.GetKeys(value.Id))
            {
                Keys.Add(key);
            }
        }
    }

    partial void OnSelectedKeyChanged(GeroImperiumKey? value)
    {
        PushKeyImageCommand.NotifyCanExecuteChanged();
        PushKeyShortcutCommand.NotifyCanExecuteChanged();
    }

    private async Task ConnectAsync()
    {
        ConnectionStatus = "Connecting to Sync Service...";

        var connectedToService = await _ipcClient.ConnectAsync();
        if (!connectedToService)
        {
            ConnectionStatus = "Sync Service not running";
            return;
        }

        try
        {
            var status = await _ipcClient.GetStatusAsync();
            IsConnected = status.IsConnected;
            ConnectionStatus = status.IsConnected ? $"Connected on {status.PortName}" : "Sync Service running, no device connected";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Failed to query Sync Service: {ex.Message}";
        }
    }

    private void Disconnect()
    {
        _ipcClient.Dispose();
        IsConnected = false;
        ConnectionStatus = "Not connected";
    }

    private async Task PushFullDatabaseAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Reading local database...";
            var bytes = await File.ReadAllBytesAsync(_databasePath);

            var progress = new Progress<IpcResponse>(p =>
            {
                ProgressPercent = p.ProgressTotalBytes == 0 ? 0 : 100.0 * p.ProgressBytesSent / p.ProgressTotalBytes;
                // ProgressStage is BulkUploadStage.ToString() from the Service (Core/Protocol/BulkUpload.cs) --
                // matched here by name so the App doesn't need a Core.Protocol reference just for this.
                StatusText = p.ProgressStage switch
                {
                    "Starting" => "Starting upload...",
                    "Uploading" => $"Uploading chunk... {p.ProgressBytesSent}/{p.ProgressTotalBytes} bytes",
                    "Finalizing" => "Finalizing -- device verifying CRC and rebooting...",
                    _ => StatusText,
                };
            });

            var result = await _ipcClient.UploadDatabaseAsync(bytes, progress);

            if (result.Success)
            {
                ProgressPercent = 100;
                StatusText = "Push complete -- device is rebooting.";
                // The device just restarted; the Service's connection to it is dead until it re-enumerates.
                IsConnected = false;
                ConnectionStatus = "Not connected (device rebooted)";
            }
            else
            {
                StatusText = $"Push failed: {result.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Push failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PushApplicationImageAsync()
    {
        if (SelectedApplication is null)
        {
            return;
        }

        if (SelectedApplication.ImageDataRgb565 is not { } imageBytes)
        {
            StatusText = $"'{SelectedApplication.Name}' has no image to push.";
            return;
        }

        var appIndex = Applications.IndexOf(SelectedApplication); // app_index = row order by Id, same as Applications' load order
        IsBusy = true;
        try
        {
            StatusText = $"Pushing image for '{SelectedApplication.Name}'...";
            var response = await _ipcClient.SetApplicationImageAsync((byte)appIndex, imageBytes);
            StatusText = response.Success ? $"Pushed '{SelectedApplication.Name}' image." : $"Push failed: {response.ErrorMessage ?? "device rejected the image"}";
        }
        catch (Exception ex)
        {
            StatusText = $"Push failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PushKeyImageAsync()
    {
        if (SelectedKeyGroup is null || SelectedKey is null)
        {
            return;
        }

        if (SelectedKey.ImageDataRgb565 is not { } imageBytes)
        {
            StatusText = $"Key {SelectedKey.Position} has no image to push.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = $"Pushing image for key {SelectedKey.Position}...";
            var response = await _ipcClient.SetKeyImageAsync((int)SelectedKeyGroup.Id, (byte)SelectedKey.Position, imageBytes);
            StatusText = response.Success ? $"Pushed key {SelectedKey.Position} image." : $"Push failed: {response.ErrorMessage ?? "device rejected the image"}";
        }
        catch (Exception ex)
        {
            StatusText = $"Push failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // TODO(doc/plan2.md 12.5/12.6): this still routes through the CDC-era IPC SetShortcutAsync (modifiers
    // byte + single token), which is being replaced by a direct REST PUT of KeyActions.TextContent. Parsing
    // back out of TextContent here is a bridge to keep this compiling against the new KeyAction shape, not
    // the final design.
    private async Task PushKeyShortcutAsync()
    {
        if (SelectedKeyGroup is null || SelectedKey is null)
        {
            return;
        }

        var action = SelectedKey.KeyActionId is long id ? _repository.GetKeyAction(id) : null;
        if (action is null || action.ActionType != ActionType.Shortcut || action.Type != HidActionKind.Hid || string.IsNullOrEmpty(action.TextContent))
        {
            StatusText = $"Key {SelectedKey.Position} has no shortcut action to push (LaunchApp/Script don't have a device-side effect yet).";
            return;
        }

        var steps = ChordSyntax.Parse(action.TextContent);
        var step = steps.Count > 0 ? steps[0] : ChordStep.Empty;

        byte modifiers = 0;
        if (step.Ctrl) modifiers |= 1 << 0;
        if (step.Shift) modifiers |= 1 << 1;
        if (step.Alt) modifiers |= 1 << 2;
        if (step.Win) modifiers |= 1 << 3;
        var shortcutToken = step.Keys.Count > 0 ? step.Keys[0] : null;

        IsBusy = true;
        try
        {
            StatusText = $"Pushing shortcut for key {SelectedKey.Position}...";
            var response = await _ipcClient.SetShortcutAsync((int)SelectedKeyGroup.Id, (byte)SelectedKey.Position, modifiers, shortcutToken);
            StatusText = response.Success ? $"Pushed key {SelectedKey.Position} shortcut." : $"Push failed: {response.ErrorMessage ?? "device rejected the shortcut"}";
        }
        catch (Exception ex)
        {
            StatusText = $"Push failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyAllCommands()
    {
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        PushFullDatabaseCommand.NotifyCanExecuteChanged();
        PushApplicationImageCommand.NotifyCanExecuteChanged();
        PushKeyImageCommand.NotifyCanExecuteChanged();
        PushKeyShortcutCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() => _ipcClient.Dispose();
}
