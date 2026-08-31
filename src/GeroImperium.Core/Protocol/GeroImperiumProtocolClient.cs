using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using GeroImperium.Core.Imaging;

namespace GeroImperium.Core.Protocol;

/// <summary>
/// Wire client for the device's CDC-ACM command protocol -- strictly synchronous request/reply, one command
/// in flight at a time (see pc_app_integration.md). Takes a Stream rather than a SerialPort directly so it
/// can be driven against a fake transport in tests; production callers pass an open SerialPort's BaseStream.
/// </summary>
public sealed class GeroImperiumProtocolClient : IDisposable
{
    private const int BulkChunkSize = 4096; // DB_UPLOAD_CHUNK_SIZE -- must not exceed this

    private static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultFinalizeReplyTimeout = TimeSpan.FromSeconds(3);

    private readonly Stream _stream;
    private readonly TimeSpan _commandTimeout;
    private readonly TimeSpan _finalizeReplyTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GeroImperiumProtocolClient(Stream stream, TimeSpan? commandTimeout = null, TimeSpan? finalizeReplyTimeout = null)
    {
        _stream = stream;
        _commandTimeout = commandTimeout ?? DefaultCommandTimeout;
        _finalizeReplyTimeout = finalizeReplyTimeout ?? DefaultFinalizeReplyTimeout;
    }

    /// <summary>'I' -- set one Applications image. appIndex is 0-based, ordered by Applications.Id.</summary>
    public async Task<bool> SetApplicationImageAsync(byte appIndex, byte[] imageRgb565, CancellationToken ct = default)
    {
        ValidateImagePayload(imageRgb565);

        var frame = new byte[1 + 1 + 4 + imageRgb565.Length];
        frame[0] = (byte)'I';
        frame[1] = appIndex;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2), (uint)imageRgb565.Length);
        imageRgb565.CopyTo(frame.AsSpan(6));

        return await SendCommandAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>'J' -- set one key image. keyGroupId is the actual KeyGroups.Id, position is 0-5.</summary>
    public async Task<bool> SetKeyImageAsync(ushort keyGroupId, byte position, byte[] imageRgb565, CancellationToken ct = default)
    {
        ValidateImagePayload(imageRgb565);

        var frame = new byte[1 + 2 + 1 + 4 + imageRgb565.Length];
        frame[0] = (byte)'J';
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), keyGroupId);
        frame[3] = position;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), (uint)imageRgb565.Length);
        imageRgb565.CopyTo(frame.AsSpan(8));

        return await SendCommandAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>'T' -- set or clear a key's shortcut. Pass null/empty shortcutToken to clear the mapping.</summary>
    public async Task<bool> SetShortcutAsync(ushort keyGroupId, byte position, ShortcutModifiers modifiers, string? shortcutToken, CancellationToken ct = default)
    {
        var tokenBytes = string.IsNullOrEmpty(shortcutToken) ? [] : Encoding.ASCII.GetBytes(shortcutToken);
        if (tokenBytes.Length > 15)
        {
            throw new ArgumentException("Shortcut token must be at most 15 bytes.", nameof(shortcutToken));
        }

        var frame = new byte[1 + 2 + 1 + 1 + 1 + tokenBytes.Length];
        frame[0] = (byte)'T';
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), keyGroupId);
        frame[3] = position;
        frame[4] = (byte)modifiers;
        frame[5] = (byte)tokenBytes.Length;
        tokenBytes.CopyTo(frame.AsSpan(6));

        return await SendCommandAsync(frame, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 'S'/'D'*/'F' -- whole-file DB replace. The device reboots on success and never sends a reply to 'F';
    /// per pc_app_integration.md this is detected by a read timeout (or the port disconnecting, which the
    /// higher-level Sync Service's presence watcher confirms once it exists -- phase 11.6). Holds the gate
    /// for the entire sequence so no other command can interleave mid-upload, since a dropped connection
    /// between 'S' and 'F' has no recovery path on the device side.
    /// </summary>
    public async Task<BulkUploadResult> UploadDatabaseAsync(byte[] databaseBytes, IProgress<BulkUploadProgress>? progress = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var crc = Crc32.HashToUInt32(databaseBytes);

            var startFrame = new byte[1 + 4 + 4];
            startFrame[0] = (byte)'S';
            BinaryPrimitives.WriteUInt32LittleEndian(startFrame.AsSpan(1), (uint)databaseBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(startFrame.AsSpan(5), crc);

            progress?.Report(new BulkUploadProgress(BulkUploadStage.Starting, 0, databaseBytes.Length));
            if (!await SendCommandCoreAsync(startFrame, ct).ConfigureAwait(false))
            {
                return BulkUploadResult.Rejected("Device rejected upload start ('S').");
            }

            var sent = 0;
            while (sent < databaseBytes.Length)
            {
                var len = Math.Min(BulkChunkSize, databaseBytes.Length - sent);
                var chunkFrame = new byte[1 + 2 + len];
                chunkFrame[0] = (byte)'D';
                BinaryPrimitives.WriteUInt16LittleEndian(chunkFrame.AsSpan(1), (ushort)len);
                databaseBytes.AsSpan(sent, len).CopyTo(chunkFrame.AsSpan(3));

                if (!await SendCommandCoreAsync(chunkFrame, ct).ConfigureAwait(false))
                {
                    return BulkUploadResult.Rejected($"Device rejected upload chunk at offset {sent}.");
                }

                sent += len;
                progress?.Report(new BulkUploadProgress(BulkUploadStage.Uploading, sent, databaseBytes.Length));
            }

            progress?.Report(new BulkUploadProgress(BulkUploadStage.Finalizing, sent, databaseBytes.Length));
            await _stream.WriteAsync(new byte[] { (byte)'F' }, ct).ConfigureAwait(false);

            return await TryReadFinalizeReplyAsync(ct).ConfigureAwait(false) switch
            {
                FinalizeOutcome.NoReply => BulkUploadResult.RebootingSuccess(),
                FinalizeOutcome.Error => BulkUploadResult.Rejected("Device rejected finalize ('F') -- size/CRC mismatch, old DB unchanged."),
                var other => throw new UnreachableException($"Unhandled {nameof(FinalizeOutcome)}: {other}"),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateImagePayload(byte[] image)
    {
        if (image.Length != Rgb565Converter.BlobSize)
        {
            throw new ArgumentException(
                $"Image payload must be exactly {Rgb565Converter.BlobSize} bytes, got {image.Length}.", nameof(image));
        }
    }

    private async Task<bool> SendCommandAsync(byte[] frame, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SendCommandCoreAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Caller must hold _gate.</summary>
    private async Task<bool> SendCommandCoreAsync(byte[] frame, CancellationToken ct)
    {
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        var reply = await ReadByteWithTimeoutAsync(_commandTimeout, ct).ConfigureAwait(false);
        return reply switch
        {
            (byte)'K' => true,
            (byte)'E' => false,
            _ => throw new ProtocolException($"Unexpected reply byte 0x{reply:X2}."),
        };
    }

    private enum FinalizeOutcome
    {
        NoReply,
        Error,
    }

    /// <summary>Caller must hold _gate.</summary>
    private async Task<FinalizeOutcome> TryReadFinalizeReplyAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_finalizeReplyTimeout);

        var buffer = new byte[1];
        int read;
        try
        {
            read = await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FinalizeOutcome.NoReply; // timed out waiting -- success per protocol doc
        }
        catch (IOException)
        {
            return FinalizeOutcome.NoReply; // port dropped -- device is rebooting, also success
        }

        if (read == 0)
        {
            return FinalizeOutcome.NoReply; // stream closed
        }

        return buffer[0] switch
        {
            (byte)'E' => FinalizeOutcome.Error,
            (byte)'K' => throw new ProtocolException("Device sent 'K' after 'F' -- the protocol never replies on success."),
            var b => throw new ProtocolException($"Unexpected byte 0x{b:X2} after 'F'."),
        };
    }

    private async Task<byte> ReadByteWithTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var buffer = new byte[1];
        int read;
        try
        {
            read = await _stream.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out waiting for the device's reply.");
        }

        if (read == 0)
        {
            throw new ProtocolException("Device closed the connection before replying.");
        }

        return buffer[0];
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}
