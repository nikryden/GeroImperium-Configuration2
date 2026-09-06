using System.IO.Pipes;
using GeroImperium.Core.Ipc;

namespace GeroImperium.Service;

/// <summary>Accepts App connections on the named pipe, one at a time (single-user tool, no concurrent App
/// instances expected). Each connection gets its own write lock, since the App-Launch push path
/// (DeviceConnectionManager.AppLaunched firing at any time) and the request/response dispatch path can both
/// try to write to the same pipe stream independently -- PipeMessageIO's length-prefix-then-payload framing
/// would interleave and corrupt if two writes raced each other unguarded.</summary>
public sealed class PipeServer
{
    private readonly DeviceConnectionManager _deviceManager;

    public PipeServer(DeviceConnectionManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(
                IpcPipeName.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await HandleClientAsync(pipe, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var writeLock = new SemaphoreSlim(1, 1);

        void OnAppLaunched(object? sender, ushort appId) => _ = PushAppLaunchEventAsync(pipe, writeLock, appId, ct);

        _deviceManager.AppLaunched += OnAppLaunched;
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await PipeMessageIO.ReadMessageAsync<IpcRequest>(pipe, ct).ConfigureAwait(false);
                if (request is null)
                {
                    break; // client disconnected
                }

                await DispatchAsync(pipe, writeLock, request, ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // client dropped the pipe mid-request -- nothing to clean up beyond disposing the pipe (caller does via `using`)
        }
        finally
        {
            _deviceManager.AppLaunched -= OnAppLaunched;
        }
    }

    private async Task DispatchAsync(NamedPipeServerStream pipe, SemaphoreSlim writeLock, IpcRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Kind)
            {
                case IpcRequestKind.GetStatus:
                    await WriteAsync(pipe, writeLock, new IpcResponse
                    {
                        Kind = IpcResponseKind.Status,
                        BleConnected = _deviceManager.IsBleConnected,
                        AppLaunchSubscribed = _deviceManager.IsAppLaunchSubscribed,
                    }, ct).ConfigureAwait(false);
                    return;

                case IpcRequestKind.RequestBleForProvisioning:
                    await _deviceManager.PauseForProvisioningAsync(ct).ConfigureAwait(false);
                    await WriteAsync(pipe, writeLock, new IpcResponse { Kind = IpcResponseKind.CommandResult, Success = true }, ct).ConfigureAwait(false);
                    return;

                case IpcRequestKind.ReleaseBleAfterProvisioning:
                    await _deviceManager.ReleaseBleAfterProvisioningAsync(ct).ConfigureAwait(false);
                    await WriteAsync(pipe, writeLock, new IpcResponse { Kind = IpcResponseKind.CommandResult, Success = true }, ct).ConfigureAwait(false);
                    return;

                default:
                    await WriteAsync(pipe, writeLock, new IpcResponse { Kind = IpcResponseKind.Error, ErrorMessage = $"Unknown request kind {request.Kind}." }, ct).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            await WriteAsync(pipe, writeLock, new IpcResponse { Kind = IpcResponseKind.Error, ErrorMessage = ex.Message }, ct).ConfigureAwait(false);
        }
    }

    private static async Task PushAppLaunchEventAsync(NamedPipeServerStream pipe, SemaphoreSlim writeLock, ushort appId, CancellationToken ct)
    {
        try
        {
            await WriteAsync(pipe, writeLock, new IpcResponse { Kind = IpcResponseKind.AppLaunchEvent, AppId = appId }, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // client already gone -- HandleClientAsync's own read loop will notice and unhook this handler
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static async Task WriteAsync(NamedPipeServerStream pipe, SemaphoreSlim writeLock, IpcResponse response, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PipeMessageIO.WriteMessageAsync(pipe, response, ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }
}
