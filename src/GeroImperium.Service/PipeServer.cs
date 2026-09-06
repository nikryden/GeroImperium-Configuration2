using System.IO.Pipes;
using GeroImperium.Core.Ipc;
using GeroImperium.Core.Protocol;

namespace GeroImperium.Service;

/// <summary>
/// Accepts App connections on the named pipe (one at a time -- the underlying device protocol is itself
/// strictly one-command-in-flight, see GeroImperiumProtocolClient) and relays each request to whatever
/// DeviceConnectionManager currently holds.
/// </summary>
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
        try
        {
            while (pipe.IsConnected && !ct.IsCancellationRequested)
            {
                var request = await PipeMessageIO.ReadMessageAsync<IpcRequest>(pipe, ct).ConfigureAwait(false);
                if (request is null)
                {
                    break; // client disconnected
                }

                await DispatchAsync(pipe, request, ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // client dropped the pipe mid-request -- nothing to clean up beyond disposing the pipe (caller does via `using`)
        }
    }

    private async Task DispatchAsync(NamedPipeServerStream pipe, IpcRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Kind)
            {
                case IpcRequestKind.GetStatus:
                    await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse
                    {
                        Kind = IpcResponseKind.Status,
                        IsConnected = _deviceManager.IsConnected,
                        PortName = _deviceManager.PortName,
                    }, ct).ConfigureAwait(false);
                    return;

                case IpcRequestKind.SetApplicationImage:
                    await DispatchDeviceCommandAsync(pipe, client => client.SetApplicationImageAsync(request.AppIndex!.Value, request.ImageRgb565!, ct), ct).ConfigureAwait(false);
                    return;

                case IpcRequestKind.SetKeyImage:
                    await DispatchDeviceCommandAsync(pipe, client => client.SetKeyImageAsync((ushort)request.KeyGroupId!.Value, request.Position!.Value, request.ImageRgb565!, ct), ct).ConfigureAwait(false);
                    return;

                case IpcRequestKind.SetShortcut:
                    await DispatchDeviceCommandAsync(pipe, client => client.SetShortcutAsync((ushort)request.KeyGroupId!.Value, request.Position!.Value, (ShortcutModifiers)request.Modifiers!.Value, request.ShortcutToken, ct), ct).ConfigureAwait(false);
                    return;

                case IpcRequestKind.UploadDatabase:
                    await DispatchUploadAsync(pipe, request.DatabaseBytes!, ct).ConfigureAwait(false);
                    return;

                default:
                    await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse { Kind = IpcResponseKind.Error, ErrorMessage = $"Unknown request kind {request.Kind}." }, ct).ConfigureAwait(false);
                    return;
            }
        }
        catch (Exception ex)
        {
            await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse { Kind = IpcResponseKind.Error, ErrorMessage = ex.Message }, ct).ConfigureAwait(false);
        }
    }

    private async Task DispatchDeviceCommandAsync(NamedPipeServerStream pipe, Func<GeroImperiumProtocolClient, Task<bool>> command, CancellationToken ct)
    {
        var client = _deviceManager.Client;
        if (client is null)
        {
            await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse { Kind = IpcResponseKind.Error, ErrorMessage = "No device connected." }, ct).ConfigureAwait(false);
            return;
        }

        var success = await command(client).ConfigureAwait(false);
        await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse { Kind = IpcResponseKind.CommandResult, Success = success }, ct).ConfigureAwait(false);
    }

    private async Task DispatchUploadAsync(NamedPipeServerStream pipe, byte[] databaseBytes, CancellationToken ct)
    {
        var client = _deviceManager.Client;
        if (client is null)
        {
            await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse { Kind = IpcResponseKind.Error, ErrorMessage = "No device connected." }, ct).ConfigureAwait(false);
            return;
        }

        // System.Progress<T> dispatches Report() asynchronously (via SynchronizationContext.Post or the
        // thread pool), which would let progress writes race each other -- and the final result write --
        // on the same pipe stream. This implementation writes synchronously so each message fully lands
        // (in order) before UploadDatabaseAsync's loop continues.
        var result = await client.UploadDatabaseAsync(databaseBytes, new SyncPipeProgress(pipe, ct), ct).ConfigureAwait(false);

        await PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse
        {
            Kind = result.Success ? IpcResponseKind.CommandResult : IpcResponseKind.Error,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Writes each progress update synchronously (blocking on the async write) so messages land on
    /// the pipe in order, never interleaved with each other or the terminal result write.</summary>
    private sealed class SyncPipeProgress(NamedPipeServerStream pipe, CancellationToken ct) : IProgress<BulkUploadProgress>
    {
        public void Report(BulkUploadProgress value)
        {
            PipeMessageIO.WriteMessageAsync(pipe, new IpcResponse
            {
                Kind = IpcResponseKind.Progress,
                ProgressStage = value.Stage.ToString(),
                ProgressBytesSent = value.BytesSent,
                ProgressTotalBytes = value.TotalBytes,
            }, ct).GetAwaiter().GetResult();
        }
    }
}
