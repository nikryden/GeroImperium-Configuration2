using System.Buffers.Binary;
using System.Text.Json;

namespace GeroImperium.Core.Ipc;

/// <summary>
/// Length-prefixed JSON framing shared by both ends of the App-Service named pipe: a 4-byte little-endian
/// length prefix followed by that many UTF-8 JSON bytes. Works over any Stream (a real NamedPipe*Stream in
/// production, a MemoryStream in tests), so the framing logic is testable without an actual pipe/process pair.
/// </summary>
public static class PipeMessageIO
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task WriteMessageAsync<T>(Stream stream, T message, CancellationToken ct = default)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Options);

        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, json.Length);

        await stream.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns default(T) if the stream was closed before a full message arrived (i.e. at a clean
    /// message boundary) -- callers use this to detect the other end hanging up.</summary>
    public static async Task<T?> ReadMessageAsync<T>(Stream stream, CancellationToken ct = default)
    {
        var lengthBuffer = new byte[4];
        if (!await ReadExactAsync(stream, lengthBuffer, ct).ConfigureAwait(false))
        {
            return default;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(payload, Options);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false; // stream closed mid-message
            }

            offset += read;
        }

        return true;
    }
}
