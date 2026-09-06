using System.IO.Pipes;

namespace GeroImperium.Core.Ipc;

/// <summary>App-side client for the Sync Service's named pipe. Unlike the old CDC-era client, this no longer
/// carries CRUD commands (those go straight from the App to the device over REST) -- its only jobs are the
/// provisioning-handoff request/response pair and demultiplexing the Service's unsolicited AppLaunchEvent
/// pushes from whatever request/response is currently in flight, via a background read loop.</summary>
public sealed class IpcDeviceClient : IDisposable
{
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private CancellationTokenSource? _readLoopCts;
    private Task? _readLoopTask;
    private TaskCompletionSource<IpcResponse>? _pendingResponse;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>Raised whenever the Service pushes an AppLaunchEvent -- the device's App-Launch characteristic
    /// notified because the user tapped an app icon. Carries the pressed app's real applications.Id.</summary>
    public event EventHandler<int>? AppLaunched;

    /// <summary>Connects to the Sync Service's pipe and starts the background read loop. Returns false (rather
    /// than throwing) if the Service isn't running -- a normal, expected condition the caller should surface,
    /// not an error.</summary>
    public async Task<bool> ConnectAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", IpcPipeName.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            return false;
        }
        catch (IOException)
        {
            pipe.Dispose();
            return false;
        }

        _pipe = pipe;
        _readLoopCts = new CancellationTokenSource();
        _readLoopTask = ReadLoopAsync(pipe, _readLoopCts.Token);
        return true;
    }

    public Task<IpcResponse> GetStatusAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Kind = IpcRequestKind.GetStatus }, ct);

    /// <summary>Asks the Service to drop its own BLE connection so this process can open its own GATT
    /// connection and drive WifiProvisioningService directly -- NimBLE allows exactly one concurrent
    /// connection. Always pair with ReleaseBleAfterProvisioningAsync (try/finally) once done, even on failure,
    /// so the Service resumes its App-Launch subscription.</summary>
    public Task<IpcResponse> RequestBleForProvisioningAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Kind = IpcRequestKind.RequestBleForProvisioning }, ct);

    public Task<IpcResponse> ReleaseBleAfterProvisioningAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequest { Kind = IpcRequestKind.ReleaseBleAfterProvisioning }, ct);

    private async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct)
    {
        if (_pipe is null)
        {
            throw new InvalidOperationException("Not connected to the Sync Service.");
        }

        // Only one request in flight at a time -- the read loop correlates by "next non-push message read"
        // rather than by any request id, so a second concurrent request would race the first for its answer.
        await _requestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponse = tcs;
            await PipeMessageIO.WriteMessageAsync(_pipe, request, ct).ConfigureAwait(false);

            using CancellationTokenRegistration registration = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingResponse = null;
            _requestGate.Release();
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                IpcResponse? response = await PipeMessageIO.ReadMessageAsync<IpcResponse>(pipe, ct).ConfigureAwait(false);
                if (response is null)
                {
                    break; // Service closed the pipe
                }

                if (response.Kind == IpcResponseKind.AppLaunchEvent)
                {
                    if (response.AppId is { } appId)
                    {
                        AppLaunched?.Invoke(this, appId);
                    }
                    continue;
                }

                _pendingResponse?.TrySetResult(response);
            }
        }
        catch (IOException)
        {
            // Service dropped the pipe -- fall through to fail any pending request below.
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pendingResponse?.TrySetException(new IOException("Sync Service closed the connection."));
        }
    }

    public void Dispose()
    {
        _readLoopCts?.Cancel();
        _pipe?.Dispose();
    }
}
