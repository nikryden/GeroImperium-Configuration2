using GeroImperium.Core.Ipc;

namespace GeroImperium.Core.Tests.Ipc;

public class PipeMessageIOTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsIpcRequest()
    {
        using var stream = new MemoryStream();
        var request = new IpcRequest
        {
            Kind = IpcRequestKind.SetKeyImage,
            KeyGroupId = 42,
            Position = 3,
            ImageRgb565 = [1, 2, 3, 4, 5],
        };

        await PipeMessageIO.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var result = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);

        Assert.NotNull(result);
        Assert.Equal(IpcRequestKind.SetKeyImage, result.Kind);
        Assert.Equal(42, result.KeyGroupId);
        Assert.Equal((byte)3, result.Position);
        Assert.Equal(request.ImageRgb565, result.ImageRgb565);
        Assert.Null(result.AppIndex);
        Assert.Null(result.ShortcutToken);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsIpcResponse()
    {
        using var stream = new MemoryStream();
        var response = new IpcResponse
        {
            Kind = IpcResponseKind.Progress,
            ProgressStage = "Uploading",
            ProgressBytesSent = 4096,
            ProgressTotalBytes = 32768,
        };

        await PipeMessageIO.WriteMessageAsync(stream, response);
        stream.Position = 0;
        var result = await PipeMessageIO.ReadMessageAsync<IpcResponse>(stream);

        Assert.NotNull(result);
        Assert.Equal(IpcResponseKind.Progress, result.Kind);
        Assert.Equal("Uploading", result.ProgressStage);
        Assert.Equal(4096, result.ProgressBytesSent);
        Assert.Equal(32768, result.ProgressTotalBytes);
    }

    [Fact]
    public async Task MultipleMessages_ReadSequentiallyWithoutMixing()
    {
        using var stream = new MemoryStream();
        await PipeMessageIO.WriteMessageAsync(stream, new IpcRequest { Kind = IpcRequestKind.GetStatus });
        await PipeMessageIO.WriteMessageAsync(stream, new IpcRequest { Kind = IpcRequestKind.UploadDatabase, DatabaseBytes = [9, 9, 9] });
        stream.Position = 0;

        var first = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);
        var second = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);

        Assert.Equal(IpcRequestKind.GetStatus, first!.Kind);
        Assert.Equal(IpcRequestKind.UploadDatabase, second!.Kind);
        Assert.Equal(new byte[] { 9, 9, 9 }, second.DatabaseBytes);
    }

    [Fact]
    public async Task ReadMessageAsync_EmptyStream_ReturnsDefault()
    {
        using var stream = new MemoryStream();

        var result = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadMessageAsync_TruncatedAfterLengthPrefix_ReturnsDefault()
    {
        using var stream = new MemoryStream();
        await stream.WriteAsync(new byte[] { 10, 0, 0, 0 }); // claims a 10-byte payload that never arrives
        stream.Position = 0;

        var result = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);

        Assert.Null(result);
    }
}
