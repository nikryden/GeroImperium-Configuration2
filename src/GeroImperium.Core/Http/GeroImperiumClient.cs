using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GeroImperium.Core.Http;

/// <summary>
/// REST client for the device's generic table CRUD + image endpoints (doc/windows_app_api_guide.md Part 1).
/// No auth (LAN-only device, accepted per doc/plan2.md's open decisions). Every call is serialized behind a
/// single-flight semaphore -- the device is one esp_http_server instance with limited RAM, hardware-confirmed
/// to take several seconds on a ~120-row table listing, and explicitly not built for concurrent requests
/// (the guide's Gotchas). Callers should not fire requests in parallel expecting a speedup; there isn't one.
/// </summary>
public sealed class GeroImperiumClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="deviceIp">Bare IP or host, no scheme (e.g. "192.168.1.22") -- as read from
    /// GET /api/status or the BLE WiFi-status characteristic.</param>
    /// <param name="handler">Test seam -- pass a fake HttpMessageHandler to avoid real hardware in unit tests.
    /// Never disposed by this client (the caller owns it); only the HttpClient wrapping it is.</param>
    /// <param name="timeout">Defaults to 60s -- hardware-confirmed the device can take several seconds to
    /// answer a large table listing, and writing a full 32768-byte image to on-device storage is slower still
    /// (hardware-observed a real upload exceeding an earlier 20s default and getting client-side cancelled,
    /// not a device error). The stock 100s HttpClient default is fine too, but callers doing a UI wait
    /// probably want a shorter, explicit bound than that.</param>
    public GeroImperiumClient(string deviceIp, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri($"http://{deviceIp}/");
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(60);

        // esp_http_server's keep-alive handling is flaky reusing a connection across requests -- hardware-
        // observed a POST immediately after a GET (e.g. Sync's first row-create right after ConnectAsync's
        // GET /api/status) failing with a device-side "Missing or oversize body" on a body only tens of bytes
        // long, which rules out an actual size problem. Forcing "Connection: close" makes every call open a
        // fresh TCP connection instead of pipelining onto whatever socket state the device's single-threaded
        // httpd was left in by the previous request.
        _http.DefaultRequestHeaders.ConnectionClose = true;
    }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            // Deliberately not GetFromJsonAsync -- it calls EnsureSuccessStatusCode() internally, which
            // throws HttpRequestException before we get a chance to read the plain-text error body
            // ourselves (the guide's Gotchas: failure bodies are plain text, not JSON). Confirmed against
            // real hardware -- GetFromJsonAsync's own exception swallows the actual error text.
            using var response = await _http.GetAsync("api/status", ct);
            await EnsureSuccessAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<DeviceStatus>(JsonOptions, ct))!;
        });

    public Task<List<T>> GetAllAsync<T>(string table, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.GetAsync($"api/{table}", ct);
            await EnsureSuccessAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<List<T>>(JsonOptions, ct))!;
        });

    /// <summary>Returns null on 404 rather than throwing -- "row doesn't exist" is an expected outcome for
    /// callers probing state, not an exceptional one.</summary>
    public Task<T?> GetAsync<T>(string table, long id, CancellationToken ct = default) where T : class =>
        SendAsync(async () =>
        {
            using var response = await _http.GetAsync($"api/{table}/{id}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, ct);
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        });

    /// <summary>body must not include Id/CreatedAt/UpdatedAt -- the server manages those (the guide's Part 1).
    /// Returns the created row, including its new Id.</summary>
    public Task<T> CreateAsync<T>(string table, object body, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.PostAsync($"api/{table}", CreateJsonContent(body), ct);
            await EnsureSuccessAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
        });

    /// <summary>Partial update -- only send the field(s) actually changing.</summary>
    public Task UpdateAsync(string table, long id, object partialBody, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.PutAsync($"api/{table}/{id}", CreateJsonContent(partialBody), ct);
            await EnsureSuccessAsync(response, ct);
            return true;
        });

    /// <summary>Builds the JSON body ourselves with a bare "application/json" Content-Type -- deliberately not
    /// PostAsJsonAsync/PutAsJsonAsync's JsonContent, which appends "; charset=utf-8". Hardware-observed: every
    /// create/update failed with the device's generic "Missing or oversized body" 400 even for a payload only
    /// tens of bytes long (ruling out an actual size problem) and even after forcing a fresh TCP connection per
    /// request (ruling out a keep-alive/socket-state problem) -- a minimal embedded HTTP server doing an exact
    /// string match on Content-Type rather than parsing the media-type header is a known-common way for the
    /// appended charset parameter to derail request parsing before the handler ever reads the body.</summary>
    private static StringContent CreateJsonContent(object body)
    {
        var json = JsonSerializer.Serialize(body, body.GetType(), JsonOptions);
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    /// <summary>404 (already gone) is treated as success -- deleting an already-deleted row achieves the
    /// caller's goal either way.</summary>
    public Task DeleteAsync(string table, long id, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.DeleteAsync($"api/{table}/{id}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }

            await EnsureSuccessAsync(response, ct);
            return true;
        });

    /// <summary>rgb565Bytes must already be in the device's storage format (inverted-channel B5G6R5 LE, see
    /// the guide's "Wire format") -- this does no conversion, that's Core.Imaging's job.</summary>
    public Task UploadImageAsync(string table, long id, byte[] rgb565Bytes, CancellationToken ct = default)
    {
        if (rgb565Bytes.Length != 32768)
        {
            throw new ArgumentException($"Image must be exactly 32768 bytes, got {rgb565Bytes.Length}.", nameof(rgb565Bytes));
        }

        return SendAsync(async () =>
        {
            using var content = new ByteArrayContent(rgb565Bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.PostAsync($"api/{table}/{id}/image", content, ct);
            await EnsureSuccessAsync(response, ct);
            return true;
        });
    }

    /// <summary>Returns null if the row has no image yet (404) rather than throwing.</summary>
    public Task<byte[]?> DownloadImageAsync(string table, long id, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.GetAsync($"api/{table}/{id}/image", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            await EnsureSuccessAsync(response, ct);
            return await response.Content.ReadAsByteArrayAsync(ct);
        });

    /// <summary>POSTs api/restart with no body to reboot the device. Callers should pass a short-lived
    /// CancellationToken rather than relying on this client's full request timeout -- the device may cut the
    /// connection the instant it starts rebooting, before ever sending a response, so a timeout/connection-drop
    /// here is the expected shape of a successful reboot, not necessarily a failure.</summary>
    public Task RestartAsync(CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.PostAsync("api/restart", null, ct);
            await EnsureSuccessAsync(response, ct);
            return true;
        });

    private async Task<T> SendAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Failure bodies are plain text, not JSON (the guide's Gotchas) -- read as a string, never
    /// ReadFromJsonAsync, or a 400/404 throws a confusing JSON-parse error instead of the real one.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new GeroImperiumApiException(response.StatusCode, body);
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
