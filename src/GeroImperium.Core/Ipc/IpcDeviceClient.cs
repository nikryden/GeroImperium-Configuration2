using System.IO.Pipes;

namespace GeroImperium.Core.Ipc;

/// <summary>
/// App-side client for the Sync Service's named pipe -- the App never opens the device's CDC port itself,
/// it relays commands through the Service (see pc_app_plan.md "Device transport").
/// </summary>
public sealed class IpcDeviceClient : IDisposable
{
    private NamedPipeClientStream? _pipe;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>Connects to the Sync Service's pipe. Returns false (rather than throwing) if the Service
    /// isn't running -- a normal, expected condition the caller should surface, not an error.</summary>
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
        return true;
    }

    public Task<IpcResponse> GetStatusAsync(CancellationToken ct = default)
        => SendAsync(new IpcRequest { Kind = IpcRequestKind.GetStatus }, ct);

    public Task<IpcResponse> SetApplicationImageAsync(byte appIndex, byte[] imageRgb565, CancellationToken ct = default)
        => SendAsync(new IpcRequest { Kind = IpcRequestKind.SetApplicationImage, AppIndex = appIndex, ImageRgb565 = imageRgb565 }, ct);

    public Task<IpcResponse> SetKeyImageAsync(int keyGroupId, byte position, byte[] imageRgb565, CancellationToken ct = default)
        => SendAsync(new IpcRequest { Kind = IpcRequestKind.SetKeyImage, KeyGroupId = keyGroupId, Position = position, ImageRgb565 = imageRgb565 }, ct);

    public Task<IpcResponse> SetShortcutAsync(int keyGroupId, byte position, byte modifiers, string? shortcutToken, CancellationToken ct = default)
        => SendAsync(new IpcRequest { Kind = IpcRequestKind.SetShortcut, KeyGroupId = keyGroupId, Position = position, Modifiers = modifiers, ShortcutToken = shortcutToken }, ct);

    /// <summary>Sends the whole-file bulk upload and relays Progress responses to the caller, returning the
    /// terminal CommandResult/Error once the Service finishes (or the device silently reboots on success).</summary>
    public async Task<IpcResponse> UploadDatabaseAsync(byte[] databaseBytes, IProgress<IpcResponse>? progress, CancellationToken ct = default)
    {
        if (_pipe is null)
        {
            throw new InvalidOperationException("Not connected to the Sync Service.");
        }

        await PipeMessageIO.WriteMessageAsync(_pipe, new IpcRequest { Kind = IpcRequestKind.UploadDatabase, DatabaseBytes = databaseBytes }, ct).ConfigureAwait(false);

        while (true)
        {
            var response = await PipeMessageIO.ReadMessageAsync<IpcResponse>(_pipe, ct).ConfigureAwait(false)
                ?? throw new IOException("Sync Service closed the connection.");

            if (response.Kind == IpcResponseKind.Progress)
            {
                progress?.Report(response);
                continue;
            }

            return response;
        }
    }

    private async Task<IpcResponse> SendAsync(IpcRequest request, CancellationToken ct)
    {
        if (_pipe is null)
        {
            throw new InvalidOperationException("Not connected to the Sync Service.");
        }

        await PipeMessageIO.WriteMessageAsync(_pipe, request, ct).ConfigureAwait(false);
        return await PipeMessageIO.ReadMessageAsync<IpcResponse>(_pipe, ct).ConfigureAwait(false)
            ?? throw new IOException("Sync Service closed the connection.");
    }

    public void Dispose() => _pipe?.Dispose();
}
