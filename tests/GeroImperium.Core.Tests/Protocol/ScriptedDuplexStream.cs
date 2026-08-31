namespace GeroImperium.Core.Tests.Protocol;

internal enum EmptyReplyBehavior
{
    /// <summary>Never satisfies a read; the reader's own timeout/cancellation must fire.</summary>
    WaitForCancellation,

    /// <summary>Read returns 0 immediately (stream closed).</summary>
    EndOfStream,

    /// <summary>Read throws IOException immediately (port dropped).</summary>
    Throw,
}

/// <summary>
/// Fake bidirectional Stream standing in for the device's CDC-ACM port. Each call to GeroImperiumProtocolClient
/// writes exactly one complete frame per WriteAsync call, so capturing each Write as one entry in WrittenFrames
/// reconstructs the command sequence without needing to parse frame boundaries here. Reply bytes are consumed
/// one at a time from a pre-loaded queue -- tests enqueue exactly what the simulated device would send back.
/// </summary>
internal sealed class ScriptedDuplexStream : Stream
{
    private readonly Queue<byte> _pendingReplyBytes = new();

    public List<byte[]> WrittenFrames { get; } = new();

    public EmptyReplyBehavior OnEmpty { get; set; } = EmptyReplyBehavior.WaitForCancellation;

    public void EnqueueReply(params byte[] bytes)
    {
        foreach (var b in bytes)
        {
            _pendingReplyBytes.Enqueue(b);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pendingReplyBytes.Count > 0)
        {
            buffer.Span[0] = _pendingReplyBytes.Dequeue();
            return 1;
        }

        switch (OnEmpty)
        {
            case EmptyReplyBehavior.EndOfStream:
                return 0;
            case EmptyReplyBehavior.Throw:
                throw new IOException("Simulated port disconnect.");
            case EmptyReplyBehavior.WaitForCancellation:
            default:
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return 0; // unreachable -- Delay throws on cancellation
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WrittenFrames.Add(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
