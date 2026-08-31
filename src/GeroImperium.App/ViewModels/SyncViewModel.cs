using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeroImperium.Core.Data;
using GeroImperium.Core.Models;
using GeroImperium.Core.Protocol;
using CoreApplication = GeroImperium.Core.Models.Application;

namespace GeroImperium.App.ViewModels;

/// <summary>
/// Push to device. Bulk S/D/F for first-push/full-profile-restore; incremental I/J/T for single-field
/// pushes -- matching the "which path to use" table in pc_app_integration.md. Opens the CDC port directly
/// for now (no Sync Service/IPC yet -- that's phase 11.6; this page is the only thing holding the port open
/// until then).
/// </summary>
public sealed partial class SyncViewModel : ObservableObject, IDisposable
{
    private readonly GeroImperiumRepository _repository;
    private readonly string _databasePath;
    private DeviceConnection? _connection;

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
        var ports = DevicePortLocator.FindDevicePorts();
        if (ports.Count == 0)
        {
            ConnectionStatus = "No device found";
            return;
        }

        try
        {
            _connection = await Task.Run(() => DeviceConnection.Open(ports[0]));
            IsConnected = true;
            ConnectionStatus = $"Connected on {ports[0]}";
        }
        catch (Exception ex)
        {
            ConnectionStatus = $"Failed to connect: {ex.Message}";
        }
    }

    private void Disconnect()
    {
        _connection?.Dispose();
        _connection = null;
        IsConnected = false;
        ConnectionStatus = "Not connected";
    }

    private async Task PushFullDatabaseAsync()
    {
        if (_connection is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Reading local database...";
            var bytes = await File.ReadAllBytesAsync(_databasePath);

            var progress = new Progress<BulkUploadProgress>(p =>
            {
                ProgressPercent = p.TotalBytes == 0 ? 0 : 100.0 * p.BytesSent / p.TotalBytes;
                StatusText = p.Stage switch
                {
                    BulkUploadStage.Starting => "Starting upload...",
                    BulkUploadStage.Uploading => $"Uploading chunk... {p.BytesSent}/{p.TotalBytes} bytes",
                    BulkUploadStage.Finalizing => "Finalizing -- device verifying CRC and rebooting...",
                    _ => StatusText,
                };
            });

            var result = await _connection.Client.UploadDatabaseAsync(bytes, progress);

            if (result.Success)
            {
                ProgressPercent = 100;
                StatusText = "Push complete -- device is rebooting.";
                // The device just restarted; the port is dead. Drop the connection rather than pretend it's live.
                _connection.Dispose();
                _connection = null;
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
        if (_connection is null || SelectedApplication is null)
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
            var ok = await _connection.Client.SetApplicationImageAsync((byte)appIndex, imageBytes);
            StatusText = ok ? $"Pushed '{SelectedApplication.Name}' image." : $"Device rejected the image for '{SelectedApplication.Name}'.";
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
        if (_connection is null || SelectedKeyGroup is null || SelectedKey is null)
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
            var ok = await _connection.Client.SetKeyImageAsync((ushort)SelectedKeyGroup.Id, (byte)SelectedKey.Position, imageBytes);
            StatusText = ok ? $"Pushed key {SelectedKey.Position} image." : $"Device rejected the image for key {SelectedKey.Position}.";
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

    private async Task PushKeyShortcutAsync()
    {
        if (_connection is null || SelectedKeyGroup is null || SelectedKey is null)
        {
            return;
        }

        var action = SelectedKey.KeyActionId is long id ? _repository.GetKeyAction(id) : null;
        if (action is null || action.ActionType != ActionType.Shortcut)
        {
            StatusText = $"Key {SelectedKey.Position} has no shortcut action to push (LaunchApp/Script don't have a device-side effect yet).";
            return;
        }

        var modifiers = ShortcutModifiers.None;
        if (action.CtrlModifier) modifiers |= ShortcutModifiers.Ctrl;
        if (action.ShiftModifier) modifiers |= ShortcutModifiers.Shift;
        if (action.AltModifier) modifiers |= ShortcutModifiers.Alt;
        if (action.WinModifier) modifiers |= ShortcutModifiers.Win;

        IsBusy = true;
        try
        {
            StatusText = $"Pushing shortcut for key {SelectedKey.Position}...";
            var ok = await _connection.Client.SetShortcutAsync((ushort)SelectedKeyGroup.Id, (byte)SelectedKey.Position, modifiers, action.ShortcutKey);
            StatusText = ok ? $"Pushed key {SelectedKey.Position} shortcut." : $"Device rejected the shortcut for key {SelectedKey.Position}.";
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

    public void Dispose() => _connection?.Dispose();
}
