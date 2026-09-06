using System.Net;
using System.Net.Http.Json;
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
    /// <param name="timeout">Defaults to 20s -- hardware-confirmed the device can take several seconds to
    /// answer a large table listing; the stock 100s HttpClient default is fine too, but callers doing a UI
    /// wait probably want a shorter, explicit bound.</param>
    public GeroImperiumClient(string deviceIp, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri($"http://{deviceIp}/");
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(20);
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
            using var response = await _http.PostAsJsonAsync($"api/{table}", body, JsonOptions, ct);
            await EnsureSuccessAsync(response, ct);
            return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct))!;
        });

    /// <summary>Partial update -- only send the field(s) actually changing.</summary>
    public Task UpdateAsync(string table, long id, object partialBody, CancellationToken ct = default) =>
        SendAsync(async () =>
        {
            using var response = await _http.PutAsJsonAsync($"api/{table}/{id}", partialBody, JsonOptions, ct);
            await EnsureSuccessAsync(response, ct);
            return true;
        });

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
