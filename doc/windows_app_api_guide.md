# GeroImperium Device API Guide (for the Windows companion app)

Audience: an AI assistant (or developer) building `GeroImperium.App` (C# / .NET 10 / WPF,
`pc-app/src/GeroImperium.App`) against the real firmware in this repo. Everything below is
verified against the actual `main/http_api.c` and `main/connectivity_manager.c` source and,
where noted, hardware-tested on 2026-09-05. No pleasantries, optimize token usage.

Two independent channels, used for different jobs:

| Channel | Used for | Requires |
|---|---|---|
| **REST over HTTP** | CRUD on the device's `Device.db` (apps, groups, keys, actions, images) | Device already on the same WiFi network, its IP |
| **BLE GATT** | First-run WiFi provisioning (no network yet) + a live app-launch push notification | Windows Bluetooth, device paired for BLE HID |

WiFi provisioning necessarily happens over BLE, since the device has no network yet. Once it's
on WiFi, everything else (CRUD, images) goes over REST -- faster, no BLE flakiness, no 20-byte
default MTU chunking concerns.

## Part 1: REST API

Base URL: `http://<device-ip>` (no HTTPS, no auth -- LAN-only device, see Gotchas). Get
`<device-ip>` either from the BLE WiFi-status characteristic (Part 2) right after provisioning,
or from the router/manual entry.

### GET /api/status

No path params. Always available (even before DB/WiFi are fully up).

```json
{
  "device": "GeroImperium",
  "uptime_ms": 436080,
  "heap_free": 8024520,
  "db_ready": true,
  "wifi": { "state": "Connected", "ip": "192.168.1.22", "rssi": -40 }
}
```

Use this to confirm reachability before anything else, and to read back the device's own IP.

### Generic CRUD: `/api/{table}[/{id}]`

One data-driven engine backs all 5 tables (`main/http_api.c`'s `table_def_t`/`column_def_t`
descriptors) -- same shape, same rules, for every table below.

| Method | Path | Action |
|---|---|---|
| GET | `/api/{table}` | List all rows, `Id` ascending |
| GET | `/api/{table}/{id}` | One row |
| POST | `/api/{table}` | Create (JSON body) -> returns the created row incl. new `Id` |
| PUT | `/api/{table}/{id}` | Partial update (JSON body, only send fields you're changing) |
| DELETE | `/api/{table}/{id}` | Delete (children cascade/null per FK, see schema.md) |

**Every row response** includes `Id`, the table's own columns (below), plus `CreatedAt`/
`UpdatedAt` (ISO 8601 strings -- see Gotchas, these are currently wrong on-device). POST/PUT
bodies never include `Id`/`CreatedAt`/`UpdatedAt` -- the server manages those.

**Success**: HTTP 200, JSON body (a row object, an array of rows, `{"deleted":true}`, or
`{"updated":true}` for image POST). **Failure**: HTTP 4xx/5xx with a **plain-text** body (not
JSON) -- e.g. `404` + `No such row`, `400` + `Missing required field 'Name'`. Check the status
code, don't assume the body parses as JSON on error.

#### Tables and columns

`table` in the URL is case-insensitive. Column names below are exact JSON field names.

**`applicationpages`** (`ApplicationsPage` -- a "page": up to 6 `Applications` shown together)
| Field | Type | Required on create | Notes |
|---|---|---|---|
| `Order` | string | yes | sort key, app convention has been plain `"1"`, `"2"`, ... |
| `Name` | string | yes | |

**`applications`** (an app icon; belongs to one page)
| Field | Type | Required | Notes |
|---|---|---|---|
| `ApplicationPageId` | int | yes | FK -> `applicationpages.Id` |
| `Order` | string | yes | position within the page |
| `Name` | string | yes | |
| `BackgroundColor` | int or null | no | RGB565 (see Colors below); drives the device's 3rd LED live when this app's page is showing |
| *(image)* | -- | -- | not a JSON field -- see Image endpoints |

**`keygroups`** (`KeyGroups` -- a "group": up to 6 `keys` shown together, belongs to one app)
| Field | Type | Required | Notes |
|---|---|---|---|
| `Order` | string | yes | |
| `Name` | string | yes | |
| `ApplicationId` | int | yes | FK -> `applications.Id` |

**`keyactions`** (`KeyActions` -- what a key press does; can be shared by multiple keys)
| Field | Type | Required | Notes |
|---|---|---|---|
| `Type` | int | yes | `0` = HID (send a chord/keystroke), `1` = raw text (type verbatim) |
| `TextContent` | string or null | no | meaning depends on `Type` -- see Chord syntax below |

**`keys`** (`GeroImperiumKeys` -- one physical key slot, position 0-5 within its group)
| Field | Type | Required | Notes |
|---|---|---|---|
| `KeyGroupId` | int | yes | FK -> `keygroups.Id` |
| `KeyActionId` | int or null | no | FK -> `keyactions.Id`; null = nothing mapped, key does nothing |
| `Position` | int | yes | 0-5, which of the 6 physical buttons in that group |
| `BackgroundColor` | int or null | no | RGB565, currently unused by firmware rendering |
| *(image)* | -- | -- | not a JSON field -- see Image endpoints |

#### `KeyActions.TextContent` chord syntax (Type = 0, HID)

A chord-sequence string, parsed by `connectivity_manager.c`'s
`connectivity_send_hid_chord_sequence()`. Grammar: the string is a concatenation of **steps**; a
new step starts at each `[` not immediately preceded by `+`. Within one step, `+`-joined tokens
are either a bracket modifier (`[ctrl]`, `[shift]`, `[alt]`, `[win]`, case-insensitive) or a bare
key (single letter/digit, `F1`-`F12`, or a named key: `LEFT RIGHT UP DOWN ENTER ESC BACKSPACE
TAB SPACE HOME END PAGEUP PAGEDOWN DELETE`).

| `TextContent` value | Meaning |
|---|---|
| `[ctrl]+c` | Ctrl+C, one chord |
| `[ctrl]+[shift]+b+n` | Ctrl+Shift+B+N, all pressed **together** (`+` = same step) |
| `[ctrl]+k[ctrl]+l` | Ctrl+K, release, then Ctrl+L (no `+` before the 2nd `[` = new step) |
| `D` | bare key, no modifier |

`[fn]` is accepted but silently dropped (no Fn bit in the standard HID boot-keyboard report --
this is a hardware limitation, not a bug to work around). For `Type = 1`, `TextContent` is typed
character-by-character exactly as given, no parsing.

#### Image endpoints

`applications` and `keys` are the two tables `has_image = true`:

- `GET /api/applications/{id}/image` / `GET /api/keys/{id}/image` -> `200`, body =
  `application/octet-stream`, exactly **32768 bytes** (128x128 pixels x 2 bytes/pixel), or `404`
  if that row has no image yet.
- `POST /api/applications/{id}/image` / `POST /api/keys/{id}/image` -> body must be exactly
  32768 raw bytes (`Content-Type: application/octet-stream`; no JSON, no base64). `404` if the
  row doesn't exist, `400` if the length is wrong.

**Wire format (critical, read before writing an encoder)**: not plain RGB565. Confirmed against
`ImageProcessingService.cs`'s real conversion pipeline and `main/db_manager.c`'s decode: each
pixel is RGB565 with **every 5/6-bit channel bitwise-inverted** (`255-x` on the original 8-bit
channel, equivalently `~x` on the truncated field), packed **B5G6R5** (not R5G6B5), written
**little-endian**. If you already have `ImageProcessingService.cs`'s `ConvertToRgb565Async` (or
equivalent) from the existing WPF app codebase, reuse it verbatim -- it already produces this
exact format for bulk DB authoring, and the REST image endpoints use the *identical* format (no
separate encoding for REST vs. bulk import). **GET and POST are symmetric**: whatever bytes GET
returns for a row are exactly the bytes POST expects back for that same row (this was a real bug
here 2026-09-05, since fixed -- see Gotchas).

#### C# example (`System.Net.Http.Json`, `.NET 10`)

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed class GeroImperiumClient(string deviceIp) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://{deviceIp}/") };

    public record Application(
        int Id, int ApplicationPageId,
        [property: JsonPropertyName("Order")] string Order,
        string Name, ushort? BackgroundColor, string CreatedAt, string UpdatedAt);

    public Task<List<Application>> GetApplicationsAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<List<Application>>("api/applications", ct)!;

    public async Task<Application> CreateApplicationAsync(int pageId, string order, string name, CancellationToken ct = default)
    {
        var body = new { ApplicationPageId = pageId, Order = order, Name = name };
        HttpResponseMessage resp = await _http.PostAsJsonAsync("api/applications", body, ct);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<Application>(cancellationToken: ct))!;
    }

    public async Task UpdateApplicationNameAsync(int id, string name, CancellationToken ct = default)
    {
        // Partial update -- only send the field(s) you're changing.
        HttpResponseMessage resp = await _http.PutAsJsonAsync($"api/applications/{id}", new { Name = name }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteApplicationAsync(int id, CancellationToken ct = default)
    {
        HttpResponseMessage resp = await _http.DeleteAsync($"api/applications/{id}", ct);
        resp.EnsureSuccessStatusCode(); // 404 throws here if it didn't exist
    }

    // rgb565Bytes must be exactly 32768 bytes, already in the device's storage format
    // (see "Wire format" above) -- do NOT send plain RGB565.
    public async Task UploadApplicationImageAsync(int id, byte[] rgb565Bytes, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(rgb565Bytes);
        content.Headers.ContentType = new("application/octet-stream");
        HttpResponseMessage resp = await _http.PostAsync($"api/applications/{id}/image", content, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<byte[]> DownloadApplicationImageAsync(int id, CancellationToken ct = default) =>
        await _http.GetByteArrayAsync($"api/applications/{id}/image", ct);

    public void Dispose() => _http.Dispose();
}
```

## Part 2: BLE

TFM: `net10.0-windows10.0.19041.0` (matches `pc-app/src/GeroImperium.WifiProvisionCli` --
**reuse that project's code directly**, it's a working, hardware-tested reference for the exact
WinRT flow below, not a throwaway). `Windows.Devices.Bluetooth` /
`Windows.Devices.Bluetooth.GenericAttributeProfile` -- no extra NuGet package needed.

Device advertises as **`"GeroImperium"`**. It only advertises/connectable while: (a) it was just
power-cycled and has an existing bond (auto-reconnect, ~300ms after boot), or (b) the user held
nav-key1 for 10s (manual pairing window). Bonding uses "Just Works" (no PIN/passkey) --
`BluetoothLEDevice.DeviceInformation.Pairing.IsPaired` becoming true is enough, no
`PairAsync`/custom ceremony needed on the C# side (Windows' own HID stack pairs it automatically
once it sees the HID service). Up to 4 concurrent bonds are supported on the device side
(`CONFIG_BT_NIMBLE_MAX_BONDS=4`).

**Device discovery**: `pc-app/src/GeroImperium.WifiProvisionCli/Program.cs`'s `FindDeviceAsync`
is the exact pattern to copy: check `DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector())`
for an already-paired match first (fast path, no advertising needed -- and once paired, NimBLE
stops advertising entirely since `CONFIG_BT_NIMBLE_MAX_CONNECTIONS=1`, so a live scan would never
find an already-connected device), only falling back to
`BluetoothLEAdvertisementWatcher` if it's genuinely never been paired.

**GATT discovery gotcha**: use `GetGattServicesAsync()`/`GetCharacteristicsAsync()` (discover-all
+ filter client-side by UUID), **not** `GetGattServicesForUuidAsync()`/
`GetCharacteristicsForUuidAsync()`. The latter use ATT "Find By Type Value" under the hood, which
reliably throws `COMException` (HRESULT `0x80070016`) against this device's 128-bit UUID
services (the custom ones below -- HID/DIS are 16-bit and work fine either way). Confirmed bug,
worked around this way in `WifiProvisionCli`.

**Retry gotcha**: the first GATT call right after connecting is flaky (Windows often hasn't
actually finished bringing the link up yet, especially since this device also exposes an HID
service and Windows' own HID/bond negotiation races your app's GATT discovery). Wrap every GATT
call in a retry loop -- `WifiProvisionCli`'s `RetryOnComExceptionAsync` (6 attempts, 2s apart) is
proven on real hardware.

### WiFi-Provisioning service

Project-defined 128-bit UUIDs (not Bluetooth SIG assigned) -- must match `connectivity_manager.c`'s
`GATT_SVC_WIFI_PROV_UUID` and friends verbatim:

| Purpose | UUID | Op |
|---|---|---|
| Service | `6E5A0001-3AE4-4D19-B7B0-4C1A2E9F0A10` | -- |
| SSID | `6E5A0002-3AE4-4D19-B7B0-4C1A2E9F0A10` | write (UTF-8 bytes, no null terminator) |
| Password | `6E5A0003-3AE4-4D19-B7B0-4C1A2E9F0A10` | write (UTF-8 bytes; empty for open network) |
| Connect trigger | `6E5A0004-3AE4-4D19-B7B0-4C1A2E9F0A10` | write-without-response, value ignored (any 1 byte) |
| Status | `6E5A0005-3AE4-4D19-B7B0-4C1A2E9F0A10` | read/notify, 7-byte blob |

**Flow**: write SSID, write password, write connect-trigger (in that order -- writing SSID/
password only stages them locally on the device; the connect-trigger write is what actually
calls `wifi_manager_set_credentials()` and kicks off the connection attempt). Then poll (or
subscribe to notify on) the status characteristic until `byte[0] == 2` (Connected) or you give
up.

**Status blob** (7 bytes, `wifi_manager.h`'s `wifi_manager_get_status_blob()`):

| Byte | Meaning |
|---|---|
| 0 | state: `0`=Idle, `1`=Connecting, `2`=Connected, `3`=AP-fallback |
| 1 | consecutive retry count |
| 2 | RSSI as **signed** int8 (meaningless unless state==Connected) |
| 3-6 | IPv4 address, **in dotted-decimal order** (byte3=1st octet, ..., byte6=4th octet) -- i.e. `$"{b[3]}.{b[4]}.{b[5]}.{b[6]}"`, not raw little-endian machine order |

`WifiProvisionCli.Program.PrintStatusBlob`/`PollStatusUntilConnectedAsync` is a working, tested
implementation of this exact parse+poll loop -- copy it rather than re-deriving.

### App-Launch service (new, 2026-09-05)

Fires when the user taps an app icon on the device itself (Level-1 action-key press) --
subscribe to this to focus/launch the corresponding app on the PC in response to a physical
button press on the device.

| Purpose | UUID | Op |
|---|---|---|
| Service | `6E5A0006-3AE4-4D19-B7B0-4C1A2E9F0A10` | -- |
| App Id | `6E5A0007-3AE4-4D19-B7B0-4C1A2E9F0A10` | read/notify, 2 bytes |

Value: the pressed app's `applications.Id` (the real database Id, matching the REST API's `Id`
field -- not a page-relative position), **uint16 little-endian**. Read it once at startup
(`ReadValueAsync`) and/or subscribe (`ValueChanged` +
`WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)`)
for live pushes while connected. Look up the corresponding row via
`GET /api/applications/{id}` to get its `Name` and decide what to launch/focus.

```csharp
ushort appId = BitConverter.ToUInt16(await ReadCharacteristicBytesAsync(appLaunchChar), 0);
```

## Gotchas / known issues (read before debugging something that isn't your bug)

- **No auth on the REST API.** Anyone on the LAN can read/write/delete. Fine for a trusted home
  network; don't expose this device's port to the internet. Not yet decided whether this
  changes (`doc/plan2.md`).
- **`CreatedAt`/`UpdatedAt` are wrong.** The device has no RTC/NTP sync, so these show
  time-since-boot from the 1970 epoch (e.g. `1970-01-01T00:15:51Z`), not wall-clock time. Not
  fixed yet -- don't rely on them for anything real.
- **Image GET/POST are now symmetric** (fixed 2026-09-05) -- earlier builds had GET return
  decoded/display pixels while POST expected raw storage-format bytes, so a naive
  round-trip (download, re-upload unchanged) silently corrupted the image. If you're working
  against an old firmware build and see this, reflash -- don't work around it client-side.
- **Don't fire many concurrent REST requests.** The device is a single ESP32-S3 running one
  `esp_http_server` instance with a handful of FreeRTOS tasks sharing limited RAM; it's built for
  a single companion app doing sequential operations, not a high-throughput API. Batch/sequence
  your calls rather than parallelizing a bulk import.
- **A `Type=0` `TextContent` with an unrecognized key token** is logged and that token is
  skipped on-device, not rejected by the REST API (the API doesn't validate chord syntax at all
  -- garbage in, silently-partial HID output out). Validate chord strings client-side if you
  want early feedback instead of discovering it at button-press time.
- **`"Order"` is a reserved SQL word** -- irrelevant to you as a REST client (the JSON field is
  just `"Order"`, a normal string), but if you ever query the on-device SQLite file directly
  (e.g. reading `Device.db` off the SD card), remember to quote it.
