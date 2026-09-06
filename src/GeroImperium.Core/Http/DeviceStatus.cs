using System.Text.Json.Serialization;

namespace GeroImperium.Core.Http;

/// <summary>GET /api/status -- always available, even before DB/WiFi are fully up. Field names are
/// snake_case on the wire (doc/windows_app_api_guide.md), unlike every other endpoint's PascalCase rows.</summary>
public sealed class DeviceStatus
{
    [JsonPropertyName("device")]
    public string Device { get; init; } = string.Empty;

    [JsonPropertyName("uptime_ms")]
    public long UptimeMs { get; init; }

    [JsonPropertyName("heap_free")]
    public long HeapFree { get; init; }

    [JsonPropertyName("db_ready")]
    public bool DbReady { get; init; }

    [JsonPropertyName("wifi")]
    public DeviceWifiStatus? Wifi { get; init; }
}

public sealed class DeviceWifiStatus
{
    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("ip")]
    public string? Ip { get; init; }

    [JsonPropertyName("rssi")]
    public int? Rssi { get; init; }
}
