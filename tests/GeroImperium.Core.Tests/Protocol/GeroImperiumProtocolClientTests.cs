using System.Buffers.Binary;
using System.Text;
using GeroImperium.Core.Imaging;
using GeroImperium.Core.Protocol;

namespace GeroImperium.Core.Tests.Protocol;

public class GeroImperiumProtocolClientTests
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(50);

    private static byte[] MakeImage(byte fill = 0xAB)
    {
        var image = new byte[Rgb565Converter.BlobSize];
        Array.Fill(image, fill);
        return image;
    }

    [Fact]
    public void Crc32_MatchesStandardCheckValue()
    {
        // "123456789" -> 0xCBF43926 is the canonical CRC-32/ISO-HDLC check value (zlib crc32, esp_rom_crc32_le
        // per pc_app_integration.md). Confirms System.IO.Hashing.Crc32 is the variant the device expects.
        var crc = System.IO.Hashing.Crc32.HashToUInt32(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public async Task SetApplicationImageAsync_WritesCorrectFrame_AndReturnsTrueOnK()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'K');
        using var client = new GeroImperiumProtocolClient(stream);
        var image = MakeImage();

        var result = await client.SetApplicationImageAsync(3, image);

        Assert.True(result);
        var frame = Assert.Single(stream.WrittenFrames);
        Assert.Equal((byte)'I', frame[0]);
        Assert.Equal((byte)3, frame[1]);
        Assert.Equal((uint)Rgb565Converter.BlobSize, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(2)));
        Assert.Equal(image, frame[6..]);
    }

    [Fact]
    public async Task SetApplicationImageAsync_ReturnsFalseOnE()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'E');
        using var client = new GeroImperiumProtocolClient(stream);

        var result = await client.SetApplicationImageAsync(0, MakeImage());

        Assert.False(result);
    }

    [Fact]
    public async Task SetApplicationImageAsync_WrongImageLength_Throws()
    {
        using var client = new GeroImperiumProtocolClient(new ScriptedDuplexStream());

        await Assert.ThrowsAsync<ArgumentException>(() => client.SetApplicationImageAsync(0, new byte[100]));
    }

    [Fact]
    public async Task SetKeyImageAsync_WritesCorrectFrame()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'K');
        using var client = new GeroImperiumProtocolClient(stream);
        var image = MakeImage(0xCD);

        var result = await client.SetKeyImageAsync(keyGroupId: 4200, position: 5, image);

        Assert.True(result);
        var frame = Assert.Single(stream.WrittenFrames);
        Assert.Equal((byte)'J', frame[0]);
        Assert.Equal((ushort)4200, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        Assert.Equal((byte)5, frame[3]);
        Assert.Equal((uint)Rgb565Converter.BlobSize, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(image, frame[8..]);
    }

    [Fact]
    public async Task SetShortcutAsync_WritesCorrectFrame_WithToken()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'K');
        using var client = new GeroImperiumProtocolClient(stream);

        var result = await client.SetShortcutAsync(
            keyGroupId: 7,
            position: 2,
            ShortcutModifiers.Ctrl | ShortcutModifiers.Shift,
            "F5");

        Assert.True(result);
        var frame = Assert.Single(stream.WrittenFrames);
        Assert.Equal((byte)'T', frame[0]);
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(1)));
        Assert.Equal((byte)2, frame[3]);
        Assert.Equal((byte)(ShortcutModifiers.Ctrl | ShortcutModifiers.Shift), frame[4]);
        Assert.Equal((byte)2, frame[5]);
        Assert.Equal("F5", Encoding.ASCII.GetString(frame, 6, 2));
    }

    [Fact]
    public async Task SetShortcutAsync_Clear_SendsZeroLengthToken()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'K');
        using var client = new GeroImperiumProtocolClient(stream);

        await client.SetShortcutAsync(1, 0, ShortcutModifiers.None, shortcutToken: null);

        var frame = Assert.Single(stream.WrittenFrames);
        Assert.Equal(6, frame.Length); // no trailing token bytes
        Assert.Equal((byte)0, frame[5]);
    }

    [Fact]
    public async Task SetShortcutAsync_TokenTooLong_Throws()
    {
        using var client = new GeroImperiumProtocolClient(new ScriptedDuplexStream());

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.SetShortcutAsync(1, 0, ShortcutModifiers.None, new string('A', 16)));
    }

    [Fact]
    public async Task UnexpectedReplyByte_ThrowsProtocolException()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'X');
        using var client = new GeroImperiumProtocolClient(stream);

        await Assert.ThrowsAsync<ProtocolException>(() => client.SetApplicationImageAsync(0, MakeImage()));
    }

    [Fact]
    public async Task NoReplyWithinTimeout_ThrowsTimeoutException()
    {
        var stream = new ScriptedDuplexStream { OnEmpty = EmptyReplyBehavior.WaitForCancellation };
        using var client = new GeroImperiumProtocolClient(stream, commandTimeout: ShortTimeout);

        await Assert.ThrowsAsync<TimeoutException>(() => client.SetApplicationImageAsync(0, MakeImage()));
    }

    [Fact]
    public async Task UploadDatabaseAsync_HappyPath_SendsStartDataFinalize_SucceedsOnSilentReboot()
    {
        var db = Encoding.ASCII.GetBytes("small database contents");
        var stream = new ScriptedDuplexStream { OnEmpty = EmptyReplyBehavior.EndOfStream };
        stream.EnqueueReply((byte)'K'); // reply to 'S'
        stream.EnqueueReply((byte)'K'); // reply to the one 'D' chunk
        // no reply queued for 'F' -- OnEmpty (EndOfStream) simulates the device rebooting silently
        using var client = new GeroImperiumProtocolClient(stream);

        var result = await client.UploadDatabaseAsync(db);

        Assert.True(result.Success);
        Assert.Equal(3, stream.WrittenFrames.Count);

        var startFrame = stream.WrittenFrames[0];
        Assert.Equal((byte)'S', startFrame[0]);
        Assert.Equal((uint)db.Length, BinaryPrimitives.ReadUInt32LittleEndian(startFrame.AsSpan(1)));
        Assert.Equal(System.IO.Hashing.Crc32.HashToUInt32(db), BinaryPrimitives.ReadUInt32LittleEndian(startFrame.AsSpan(5)));

        var dataFrame = stream.WrittenFrames[1];
        Assert.Equal((byte)'D', dataFrame[0]);
        Assert.Equal((ushort)db.Length, BinaryPrimitives.ReadUInt16LittleEndian(dataFrame.AsSpan(1)));
        Assert.Equal(db, dataFrame[3..]);

        var finalizeFrame = stream.WrittenFrames[2];
        Assert.Equal(new byte[] { (byte)'F' }, finalizeFrame);
    }

    [Fact]
    public async Task UploadDatabaseAsync_SplitsIntoChunksOf4096()
    {
        var db = new byte[5000];
        Random.Shared.NextBytes(db);
        var stream = new ScriptedDuplexStream { OnEmpty = EmptyReplyBehavior.EndOfStream };
        stream.EnqueueReply((byte)'K'); // 'S'
        stream.EnqueueReply((byte)'K'); // first 4096-byte 'D'
        stream.EnqueueReply((byte)'K'); // second 904-byte 'D'
        using var client = new GeroImperiumProtocolClient(stream);

        var result = await client.UploadDatabaseAsync(db);

        Assert.True(result.Success);
        Assert.Equal(4, stream.WrittenFrames.Count); // S + 2 D + F

        var firstChunk = stream.WrittenFrames[1];
        Assert.Equal((ushort)4096, BinaryPrimitives.ReadUInt16LittleEndian(firstChunk.AsSpan(1)));
        Assert.Equal(db[..4096], firstChunk[3..]);

        var secondChunk = stream.WrittenFrames[2];
        Assert.Equal((ushort)904, BinaryPrimitives.ReadUInt16LittleEndian(secondChunk.AsSpan(1)));
        Assert.Equal(db[4096..], secondChunk[3..]);
    }

    [Fact]
    public async Task UploadDatabaseAsync_RejectedAtStart_SendsNoDataOrFinalize()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'E'); // 'S' rejected
        using var client = new GeroImperiumProtocolClient(stream);

        var result = await client.UploadDatabaseAsync(Encoding.ASCII.GetBytes("db"));

        Assert.False(result.Success);
        Assert.Single(stream.WrittenFrames); // only 'S' was sent
    }

    [Fact]
    public async Task UploadDatabaseAsync_RejectedAtFinalize_ReportsFailureWithoutThrowing()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'K'); // 'S'
        stream.EnqueueReply((byte)'K'); // 'D'
        stream.EnqueueReply((byte)'E'); // 'F' rejected -- CRC/size mismatch, old DB stays active

        using var client = new GeroImperiumProtocolClient(stream);

        var result = await client.UploadDatabaseAsync(Encoding.ASCII.GetBytes("db"));

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task UploadDatabaseAsync_KAfterFinalize_ThrowsProtocolException()
    {
        var stream = new ScriptedDuplexStream();
        stream.EnqueueReply((byte)'K'); // 'S'
        stream.EnqueueReply((byte)'K'); // 'D'
        stream.EnqueueReply((byte)'K'); // 'F' -- protocol doc says this should never happen on success

        using var client = new GeroImperiumProtocolClient(stream);

        await Assert.ThrowsAsync<ProtocolException>(() => client.UploadDatabaseAsync(Encoding.ASCII.GetBytes("db")));
    }
}
