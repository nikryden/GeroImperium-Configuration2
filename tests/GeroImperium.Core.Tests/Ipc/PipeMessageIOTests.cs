using GeroImperium.Core.Ipc;

namespace GeroImperium.Core.Tests.Ipc;

public class PipeMessageIOTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsIpcRequest()
    {
        using var stream = new MemoryStream();
        var request = new IpcRequest { Kind = IpcRequestKind.RequestBleForProvisioning };

        await PipeMessageIO.WriteMessageAsync(stream, request);
        stream.Position = 0;
        var result = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);

        Assert.NotNull(result);
        Assert.Equal(IpcRequestKind.RequestBleForProvisioning, result.Kind);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsIpcResponse()
    {
        using var stream = new MemoryStream();
        var response = new IpcResponse
        {
            Kind = IpcResponseKind.Status,
            BleConnected = true,
            AppLaunchSubscribed = true,
        };

        await PipeMessageIO.WriteMessageAsync(stream, response);
        stream.Position = 0;
        var result = await PipeMessageIO.ReadMessageAsync<IpcResponse>(stream);

        Assert.NotNull(result);
        Assert.Equal(IpcResponseKind.Status, result.Kind);
        Assert.True(result.BleConnected);
        Assert.True(result.AppLaunchSubscribed);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAppLaunchEvent()
    {
        using var stream = new MemoryStream();
        var response = new IpcResponse { Kind = IpcResponseKind.AppLaunchEvent, AppId = 42 };

        await PipeMessageIO.WriteMessageAsync(stream, response);
        stream.Position = 0;
        var result = await PipeMessageIO.ReadMessageAsync<IpcResponse>(stream);

        Assert.NotNull(result);
        Assert.Equal(IpcResponseKind.AppLaunchEvent, result.Kind);
        Assert.Equal(42, result.AppId);
    }

    [Fact]
    public async Task MultipleMessages_ReadSequentiallyWithoutMixing()
    {
        using var stream = new MemoryStream();
        await PipeMessageIO.WriteMessageAsync(stream, new IpcRequest { Kind = IpcRequestKind.GetStatus });
        await PipeMessageIO.WriteMessageAsync(stream, new IpcRequest { Kind = IpcRequestKind.ReleaseBleAfterProvisioning });
        stream.Position = 0;

        var first = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);
        var second = await PipeMessageIO.ReadMessageAsync<IpcRequest>(stream);

        Assert.Equal(IpcRequestKind.GetStatus, first!.Kind);
        Assert.Equal(IpcRequestKind.ReleaseBleAfterProvisioning, second!.Kind);
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
