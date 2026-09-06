using GeroImperium.Core.Ble;

namespace GeroImperium.Service;

/// <summary>Owns the device's one BLE connection slot (NimBLE allows exactly one concurrent GATT connection --
/// doc/plan2.md's "Why the Service still exists"). Runs a background reconnect loop that connects and
/// subscribes to AppLaunchService whenever the device is reachable and provisioning isn't in progress; the App
/// borrows the slot via PauseForProvisioningAsync/ReleaseBleAfterProvisioningAsync (through PipeServer) rather
/// than ever holding a competing connection of its own at the same time.</summary>
public sealed class DeviceConnectionManager : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _reconnectLoopTask;

    private DeviceBleClient? _bleClient;
    private AppLaunchService? _appLaunch;
    private bool _pausedForProvisioning;

    public bool IsBleConnected => _bleClient?.IsConnected ?? false;
    public bool IsAppLaunchSubscribed { get; private set; }

    public event EventHandler? StatusChanged;

    /// <summary>The pressed app's real applications.Id, forwarded from AppLaunchService.</summary>
    public event EventHandler<ushort>? AppLaunched;

    public DeviceConnectionManager()
    {
        _reconnectLoopTask = ReconnectLoopAsync(_lifetimeCts.Token);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TryConnectAndSubscribeAsync(ct).ConfigureAwait(false);
            try
            {
                await Task.Delay(ReconnectInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Connect and subscribe are retried independently -- a connect that succeeds but whose
    /// subscribe step fails (real, hardware-observed: Windows can return a non-success GATT status right
    /// after a fresh connection without throwing, so BleRetry's COMException-only retry doesn't cover it)
    /// must not get stuck forever just because IsBleConnected is already true. Every tick re-attempts
    /// whichever half (connect, subscribe) isn't done yet, reusing the existing connection if it's already up.</summary>
    private async Task TryConnectAndSubscribeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pausedForProvisioning)
            {
                return;
            }

            if (_bleClient is null || !_bleClient.IsConnected)
            {
                await DisconnectCoreAsync(ct).ConfigureAwait(false);

                var client = new DeviceBleClient();
                bool connected = await client.ConnectAsync(ct: ct).ConfigureAwait(false);
                if (!connected)
                {
                    client.Dispose();
                    return;
                }

                _bleClient = client;
                _appLaunch = new AppLaunchService(client);
                _appLaunch.AppLaunched += OnAppLaunched;
            }

            if (!IsAppLaunchSubscribed && _appLaunch is not null)
            {
                IsAppLaunchSubscribed = await _appLaunch.SubscribeAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Transient BLE flakiness (e.g. mid-negotiation on a fresh connect, or the device briefly out of
            // range) -- the next reconnect tick (at most ReconnectInterval away) retries.
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnAppLaunched(object? sender, ushort appId) => AppLaunched?.Invoke(this, appId);

    /// <summary>Tears down the Service's own BLE connection so the App can open its own GATT connection and
    /// drive WifiProvisioningService directly -- both sides can never hold NimBLE's one connection slot at
    /// once. The reconnect loop is paused, not stopped; call ReleaseBleAfterProvisioningAsync (even on
    /// failure, via try/finally on the App side) to resume it.</summary>
    public async Task PauseForProvisioningAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _pausedForProvisioning = true;
            await DisconnectCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Un-pauses and reconnects immediately, rather than waiting for the next reconnect tick.</summary>
    public Task ReleaseBleAfterProvisioningAsync(CancellationToken ct = default)
    {
        _pausedForProvisioning = false;
        return TryConnectAndSubscribeAsync(ct);
    }

    /// <summary>Explicitly unsubscribes (writes CCCD None) before tearing the connection down, rather than
    /// just disposing AppLaunchService -- hardware-observed that skipping the explicit unsubscribe (relying
    /// only on the disconnect to implicitly clear it) left the very next reconnect's re-subscribe attempt
    /// failing repeatedly against this bonded device, as if the device/Windows' cached notify state didn't
    /// get cleared by the disconnect alone.</summary>
    private async Task DisconnectCoreAsync(CancellationToken ct)
    {
        if (_appLaunch is not null)
        {
            try
            {
                await _appLaunch.UnsubscribeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort -- the connection is coming down regardless (see below), and a fresh connection
                // won't inherit whatever confused this unsubscribe write.
            }

            _appLaunch.AppLaunched -= OnAppLaunched;
            _appLaunch.Dispose();
            _appLaunch = null;
        }

        _bleClient?.Dispose();
        _bleClient = null;
        IsAppLaunchSubscribed = false;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts.Cancel();
        try
        {
            await _reconnectLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
