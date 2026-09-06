using System.Net;
using System.Net.Http.Json;
using System.Text;
using GeroImperium.Core.Http;

namespace GeroImperium.Core.Tests.Http;

public class GeroImperiumClientTests
{
    private static GeroImperiumClient CreateClient(FakeHttpMessageHandler handler) =>
        new("192.168.1.22", handler, TimeSpan.FromSeconds(5));

    [Fact]
    public async Task GetStatusAsync_ParsesSnakeCaseFields()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("http://192.168.1.22/api/status", req.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"device":"GeroImperium","uptime_ms":436080,"heap_free":8024520,"db_ready":true,"wifi":{"state":"Connected","ip":"192.168.1.22","rssi":-40}}""",
                    Encoding.UTF8, "application/json"),
            });
        });
        using var client = CreateClient(handler);

        var status = await client.GetStatusAsync();

        Assert.Equal("GeroImperium", status.Device);
        Assert.Equal(436080, status.UptimeMs);
        Assert.True(status.DbReady);
        Assert.Equal("Connected", status.Wifi!.State);
        Assert.Equal(-40, status.Wifi.Rssi);
    }

    [Fact]
    public async Task GetAllAsync_DeserializesRowList()
    {
        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal("http://192.168.1.22/api/applications", req.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[]
                {
                    new ApplicationRow(1, 1, "1", "Discord", null, "1970-01-01T00:00:00Z", "1970-01-01T00:00:00Z"),
                }),
            });
        });
        using var client = CreateClient(handler);

        var apps = await client.GetAllAsync<ApplicationRow>(GeroImperiumTables.Applications);

        var app = Assert.Single(apps);
        Assert.Equal("Discord", app.Name);
    }

    [Fact]
    public async Task GetAsync_NotFound_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No such row"),
        }));
        using var client = CreateClient(handler);

        var app = await client.GetAsync<ApplicationRow>(GeroImperiumTables.Applications, 999);

        Assert.Null(app);
    }

    [Fact]
    public async Task CreateAsync_PostsBodyAndReturnsCreatedRow()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            captured = req;
            Assert.Equal(HttpMethod.Post, req.Method);
            var sentJson = await req.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("\"Id\"", sentJson);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApplicationRow(7, 1, "1", "Slack", null, null, null)),
            };
        });
        using var client = CreateClient(handler);

        var created = await client.CreateAsync<ApplicationRow>(
            GeroImperiumTables.Applications,
            new { ApplicationPageId = 1, Order = "1", Name = "Slack" });

        Assert.Equal(7, created.Id);
        Assert.Equal("Slack", created.Name);
        Assert.Equal("http://192.168.1.22/api/applications", captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task UpdateAsync_SendsPutToIdPath()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"updated\":true}") });
        });
        using var client = CreateClient(handler);

        await client.UpdateAsync(GeroImperiumTables.Applications, 3, new { Name = "Renamed" });

        Assert.Equal(HttpMethod.Put, captured!.Method);
        Assert.Equal("http://192.168.1.22/api/applications/3", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task DeleteAsync_NotFound_DoesNotThrow()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No such row"),
        }));
        using var client = CreateClient(handler);

        await client.DeleteAsync(GeroImperiumTables.Applications, 999);
    }

    [Fact]
    public async Task NonSuccessResponse_SurfacesPlainTextBody_NotAJsonParseError()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("Missing required field 'Name'"),
        }));
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<GeroImperiumApiException>(
            () => client.CreateAsync<ApplicationRow>(GeroImperiumTables.Applications, new { }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("Missing required field 'Name'", ex.ResponseBody);
    }

    [Fact]
    public async Task GetAllAsync_NonSuccessResponse_ThrowsApiExceptionNotHttpRequestException()
    {
        // Regression test: GetFromJsonAsync calls EnsureSuccessStatusCode() internally and throws
        // HttpRequestException *before* GeroImperiumClient gets a chance to read the plain-text error body
        // -- confirmed against real hardware (a bogus table name 404s and this exact bug surfaced).
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No such table"),
        }));
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<GeroImperiumApiException>(
            () => client.GetAllAsync<ApplicationRow>("not_a_real_table"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("No such table", ex.ResponseBody);
    }

    [Fact]
    public async Task GetStatusAsync_NonSuccessResponse_ThrowsApiExceptionNotHttpRequestException()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("DB not ready"),
        }));
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<GeroImperiumApiException>(() => client.GetStatusAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.Equal("DB not ready", ex.ResponseBody);
    }

    [Fact]
    public async Task UploadImageAsync_WrongLength_ThrowsWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new InvalidOperationException("should not be called"));
        using var client = CreateClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.UploadImageAsync(GeroImperiumTables.Applications, 1, new byte[100]));
    }

    [Fact]
    public async Task UploadImageAsync_CorrectLength_PostsOctetStream()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(async req =>
        {
            captured = req;
            var bytes = await req.Content!.ReadAsByteArrayAsync();
            Assert.Equal(32768, bytes.Length);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"updated\":true}") };
        });
        using var client = CreateClient(handler);

        await client.UploadImageAsync(GeroImperiumTables.Applications, 1, new byte[32768]);

        Assert.Equal("application/octet-stream", captured!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("http://192.168.1.22/api/applications/1/image", captured.RequestUri!.ToString());
    }

    [Fact]
    public async Task DownloadImageAsync_ReturnsExactBytes()
    {
        var expected = Enumerable.Range(0, 32768).Select(i => (byte)i).ToArray();
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expected),
        }));
        using var client = CreateClient(handler);

        var bytes = await client.DownloadImageAsync(GeroImperiumTables.Applications, 1);

        Assert.Equal(expected, bytes);
    }

    [Fact]
    public async Task DownloadImageAsync_NotFound_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = CreateClient(handler);

        var bytes = await client.DownloadImageAsync(GeroImperiumTables.Keys, 1);

        Assert.Null(bytes);
    }

    [Fact]
    public async Task RestartAsync_PostsToRestartEndpointWithNoBody()
    {
        HttpRequestMessage? captured = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            captured = req;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = CreateClient(handler);

        await client.RestartAsync();

        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("http://192.168.1.22/api/restart", captured.RequestUri!.ToString());
        Assert.Null(captured.Content);
    }

    [Fact]
    public async Task RestartAsync_NonSuccessResponse_ThrowsApiException()
    {
        var handler = new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("No such endpoint"),
        }));
        using var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<GeroImperiumApiException>(() => client.RestartAsync());

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Requests_AreSerializedNeverConcurrent()
    {
        var maxConcurrent = 0;
        var current = 0;
        var gate = new object();

        var handler = new FakeHttpMessageHandler(async _ =>
        {
            lock (gate)
            {
                current++;
                maxConcurrent = Math.Max(maxConcurrent, current);
            }

            await Task.Delay(50);

            lock (gate)
            {
                current--;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        });
        using var client = CreateClient(handler);

        await Task.WhenAll(
            client.GetAllAsync<ApplicationRow>(GeroImperiumTables.Applications),
            client.GetAllAsync<ApplicationRow>(GeroImperiumTables.Applications),
            client.GetAllAsync<ApplicationRow>(GeroImperiumTables.Applications));

        Assert.Equal(1, maxConcurrent);
    }
}
