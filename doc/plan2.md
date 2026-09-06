## Plan2: Ditch USB CDC, move to REST (WiFi) + BLE (provisioning / app-launch)

Supersedes the transport layer of [pc_app_plan.md](pc_app_plan.md). Trigger: USB CDC framing
(`S`/`D`/`F` bulk, `I`/`J`/`T` incremental, CRC32, COM-port ownership fights) is being dropped by
firmware in favor of REST-over-WiFi for CRUD/images and BLE GATT for WiFi provisioning + a
live app-launch push. Wire-level source of truth is [windows_app_api_guide.md](windows_app_api_guide.md)
(hardware-verified 2026-09-05) -- read that first, this document is the migration plan on top of it.

Operating rule carried over: no pleasantries, optimize token usage. Sub-agents only for
independently-executable slices (a single new client class + its tests, a ViewModel swap) --
not for the schema/sync-model decisions below.

### Roles and responsibilities

Reuses [plan.md](plan.md)'s team, re-scoped to this migration. USB Expert's charter (USB CDC
reliability/upload protocol) is retired for this plan -- its BLE-adjacent responsibilities fold
into Bluetooth Expert below.

1. **PC Application Lead**
   * Owns the WPF App and Service code changes end to end: `Core/Http`, the `SyncViewModel`
     rewrite, the Applications/Key editor UI changes (page/order editing, chord-based shortcut
     editor), and the Service's provisioning-handoff/App-Launch-listener rewrite.
   * Decides the diff-and-push sync model's concrete implementation (`RemoteId` tracking,
     tombstone strategy) once the open decisions below are resolved.
2. **Data & Sync Protocol Owner**
   * Owns the schema migration (`ApplicationPages`, `Order`/`RemoteId` columns, `KeyActions.Type`/
     `TextContent`) and keeps the local authoring cache's shape a correct subset/superset of the
     device's real REST schema as documented in windows_app_api_guide.md.
   * Owns `ChordSyntax`'s grammar conformance -- the one place a chord-string bug would silently
     misfire a button on-device instead of failing loudly.
   * Re-evaluates firmware-gap phases (per-key LaunchApp/Script, storage `'M'`/REST endpoint) as
     firmware evolves, and keeps plan.md's phases 13-15 in sync with whatever this document
     concludes.
3. **Bluetooth Expert**
   * Owns `Core/Ble`: the port of `WifiProvisionCli`'s discovery/retry/GATT patterns into
     `DeviceBleClient`, `WifiProvisioningService`, `AppLaunchService`.
   * Owns the BLE single-connection arbitration contract between the Service (App-Launch
     subscription) and the App (provisioning handoff) -- the one area a mistake here causes a
     silent, hard-to-repro failure (both sides idle, or both trying to hold the connection).
4. **Senior Embedded Developer**
   * Supports the Service rewrite (tray icon state, `DeviceConnectionManager` on BLE) alongside
     the PC Application Lead, and supports `Core/Http`'s sequencing discipline against the
     device's single-`esp_http_server` limitation.
5. **QA/Test Automation**
   * Owns hardware-only verification gates (BLE provisioning round trip, App-Launch end-to-end,
     sync-survives-mid-disconnect) that can't be covered by unit tests against fakes.
6. **Project Lead**
   * Signs off on each phase before it's marked Done, per plan.md's existing release-readiness
     ownership.

### What this changes vs. what it doesn't

**Gone entirely**: the serial/CDC transport concept. No more "one port, one owner, one
in-flight command", no CRC32, no chunked bulk-file push, no VID/PID COM-port enumeration.

**Unchanged**: `Core/Imaging` (`Rgb565Converter`/`ImageCompositor`/`SvgRasterizer`/
`ImagePipeline`) -- the guide confirms byte-for-byte the same wire format (invert + B5G6R5,
little-endian, 32768 bytes) is used for the REST image endpoints, so this whole subsystem is
reused verbatim. The WPF shell, MVVM structure, `CommunityToolkit.Mvvm` usage, and the
Applications/Key Groups editor *pages* (not their sync plumbing) also carry over unchanged.

### Why the Service still exists

REST over HTTP has no port-exclusivity problem -- unlike the old CDC port, the App could talk to
the device directly and the whole IPC/Service layer could theoretically disappear. It can't,
for one hard reason: **NimBLE on the device allows exactly one concurrent GATT connection**
(`CONFIG_BT_NIMBLE_MAX_CONNECTIONS=1`). The App-Launch service (Part 2 of the guide) needs a
long-lived BLE connection held continuously to receive live notifications, and WiFi provisioning
also needs BLE. Both can't hold it at once, and whichever one is idle would otherwise steal the
connection out from under the other. So:

* The **Service** becomes the sole long-lived BLE holder: it maintains the App-Launch
  subscription whenever the device is paired and reachable, and does `Process.Start` on receipt
  (unchanged intent from pc_app_plan.md's Sync Service section, just a different trigger source).
* REST CRUD/image calls move **out of the Service and directly into the App** -- no reason to
  proxy them, HTTP has no shared-handle problem. The device's single-`esp_http_server` gotcha
  ("don't fire many concurrent requests") is a sequencing discipline inside the App's own REST
  client, not a cross-process ownership problem.
* IPC (named pipe, `Core/Ipc`) is **repurposed, not deleted**: its job changes from "relay CRUD
  commands to the port I own" to "let the App borrow the BLE connection for provisioning /
  IP-refresh, and stream App-Launch events to the App's UI for a live activity indicator."

### Data model changes (local SQLite authoring cache vs. the real device schema)

The guide's schema differs from the local cache built for the CDC world in three ways that
matter:

| Gap | Current local schema | Device (per guide) | Fix |
|---|---|---|---|
| No page grouping | `Applications` has no page concept | `ApplicationPages` (`Order`, `Name`) is a real table; `Applications.ApplicationPageId` + `Applications.Order` are required FKs | Add `ApplicationPages` table; add `ApplicationPageId`, `Order` to `Applications` |
| No group ordering | `KeyGroups` has no `Order` | `KeyGroups.Order` required on create | Add `KeyGroups.Order` |
| Shortcut shape | `KeyActions.ShortcutKey` + 4 bool modifier columns, CDC-specific | `KeyActions.Type` (0=HID,1=RawText) + `TextContent` (chord-syntax string, see guide) | Replace modifier bools with `Type`/`TextContent`; keep a structured editor (key + checkboxes) locally and *compile* it to `TextContent` at sync time (see Chord module below) -- don't force the WPF editor UX to become a raw text box |
| `GeneralSettings` | One of "the 5 tables" in the old CDC doc | **Not one of the 5 REST tables** in the new guide | Treat as app-local-only henceforth (theme, cached device IP/BLE id); stop pretending it round-trips to the device. Drop `DefaultDevicePort`, add `DeviceIpAddress`, `LastKnownBleDeviceId` |
| Table naming | `GeroImperiumKeys` | REST path is `keys` (case-insensitive) | Cosmetic only -- keep local table/model name `GeroImperiumKeys`, map to path `keys` in the REST client's table-name constant |

`RemoteId` mirror columns (nullable `long`, one per synced table: `Applications.RemoteId`,
`ApplicationPages.RemoteId`, `KeyGroups.RemoteId`, `KeyActions.RemoteId`,
`GeroImperiumKeys.RemoteId`) are new and load-bearing for the sync model below -- without them
the app can't tell "needs POST" (new, no `RemoteId`) from "needs PUT" (`RemoteId` set) from
"needs DELETE" (existed with a `RemoteId`, row now gone locally).

### Core/Http -- REST client (new)

One class, `GeroImperiumClient` (`Core/Http`), matching the guide's own example almost verbatim:

* `HttpClient` with `BaseAddress = http://{deviceIp}/`, no auth headers (device has none).
* Generic CRUD over the table-driven engine: `GetAllAsync<T>(table)`, `GetAsync<T>(table, id)`,
  `CreateAsync<T>(table, body)`, `UpdateAsync(table, id, partialBody)`, `DeleteAsync(table, id)`.
  Table name is a string constant per model (`"applicationpages"`, `"applications"`,
  `"keygroups"`, `"keyactions"`, `"keys"`) -- one lookup table, not five hand-written methods.
* `GetStatusAsync()` -> `{device, uptime_ms, heap_free, db_ready, wifi:{state, ip, rssi}}` --
  used both as the reachability probe and to read back the device's own IP after any network
  change.
* `UploadImageAsync(table, id, rgb565Bytes)` / `DownloadImageAsync(table, id)` for
  `applications`/`keys` only -- raw octet-stream, no JSON wrapper, exactly 32768 bytes.
- Error handling: HTTP failure bodies are **plain text, not JSON** (per guide) -- never
  `ReadFromJsonAsync` on a non-2xx response; surface status code + text body as the exception
  message.
- Sequencing: an internal `SemaphoreSlim(1,1)` around every call. The device is a single
  `esp_http_server` instance -- this client must never let two requests race, even from
  independent App code paths (e.g. a background poll and a user-triggered sync overlapping).

### Core/Ble -- BLE client (new, ports `GeroImperium.WifiProvisionCli`)

The guide is explicit: `pc-app/src/GeroImperium.WifiProvisionCli` is a working, hardware-tested
reference and should be reused directly, not re-derived. Lift its patterns into `Core/Ble`:

* `DeviceBleClient`: paired-first discovery (`DeviceInformation.FindAllAsync` +
  `BluetoothLEDevice.GetDeviceSelector()`), falling back to `BluetoothLEAdvertisementWatcher`
  only if never paired. Advertises as `"GeroImperium"`.
* `GetGattServicesAsync()`/`GetCharacteristicsAsync()` + client-side UUID filtering -- **not**
  the `ForUuid` variants (confirmed `COMException 0x80070016` against this device's 128-bit
  custom UUIDs).
* `RetryOnComExceptionAsync` wrapper (6 attempts, 2s apart) around every GATT call -- the first
  call after connect races Windows' own HID/bond negotiation.
* Two service wrappers:
  * `WifiProvisioningService`: write SSID -> write password -> write connect-trigger (strict
    order, connect-trigger is what actually fires `wifi_manager_set_credentials()`), then
    poll/notify the 7-byte status characteristic until `state == Connected` (or timeout). Parses
    the IPv4 bytes in the documented dotted-decimal (not little-endian) order.
  * `AppLaunchService`: read-once + subscribe-to-notify on the 2-byte little-endian App Id
    characteristic; raises a C# event with the parsed `ushort` app id.
* UUIDs are `const`s copied verbatim from the guide's Part 2 table -- do not regenerate.

TFM note: BLE (`Windows.Devices.Bluetooth`) requires the versioned Windows TFM. Bump
`GeroImperium.Core.csproj`'s `TargetFramework` from `net10.0-windows` to
`net10.0-windows10.0.19041.0` (matches `WifiProvisionCli`); `App`/`Service` projects inherit
the requirement transitively and should move to the same TFM. No new NuGet package needed --
`Windows.Devices.Bluetooth*` is a WinRT projection available once the TFM carries the SDK
version suffix.

### Chord syntax module (new, replaces `ShortcutModifiers`)

`KeyActions.TextContent` is a string grammar (steps of `+`-joined bracket-modifiers/bare-keys, a
new step at each `[` not preceded by `+`), not a modifiers-byte + single-key pair. The WPF key
editor should keep its existing structured UX (modifier checkboxes + a key picker, one step at a
time -- possibly multiple steps as an ordered list for multi-chord sequences like
`[ctrl]+k[ctrl]+l`), and a new `Core/Http/ChordSyntax` module handles the two directions:

* `ChordSyntax.Build(IEnumerable<ChordStep> steps) -> string` -- compiles the editor's
  structured steps into a `TextContent` string.
* `ChordSyntax.TryParse(string textContent, out IReadOnlyList<ChordStep> steps)` -- for
  displaying a `TextContent` pulled from the device (or an older local row) back in the
  structured editor.
* `ChordSyntax.Validate(string textContent) -> IReadOnlyList<string> unknownTokens` -- client-side
  check against the documented bare-key vocabulary (letters/digits, `F1`-`F12`,
  `LEFT RIGHT UP DOWN ENTER ESC BACKSPACE TAB SPACE HOME END PAGEUP PAGEDOWN DELETE`) and bracket
  modifiers (`ctrl`/`shift`/`alt`/`win`, `fn` accepted-but-flagged-as-dropped). The REST API does
  not validate chord syntax at all (guide's Gotchas) -- garbage silently drops tokens at
  button-press time on-device, so this is the only place a typo gets caught.
* `Type = 1` (raw text) bypasses this module entirely -- `TextContent` is typed verbatim,
  no parsing.

`ShortcutModifiers` (bitmask enum) and the `KeyActions` modifier-bool columns are removed; the
structured editor's in-memory step list is the new equivalent, only compiled to the wire string
at sync time.

### Sync model: from "push the whole DB file" to "diff and push rows"

The old model treated the local SQLite file as *the* device DB and bulk-pushed it byte-for-byte
(`S`/`D`/`F`). There is no equivalent REST endpoint -- the device's own `Device.db` is the
source of truth, reached one row/image at a time. New model:

1. Local SQLite stays as an **authoring cache** (offline editing, image compositing, undo-able
   drafts) -- not a mirror pushed verbatim.
2. `SyncViewModel`'s "push" walks tables in FK order (`ApplicationPages` -> `Applications`
   [+images] -> `KeyGroups` -> `KeyActions` -> `GeroImperiumKeys` [+images]) and, per row: `POST`
   if `RemoteId` is null (store the returned `Id` back into `RemoteId`), `PUT` if it's set and
   the row changed, skip if unchanged. Rows locally deleted since the last sync (tracked by a
   `RemoteId` that no longer has a matching local row -- keep a tombstone list, or simplest:
   diff against a snapshot of `RemoteId`s taken at the end of the last successful sync) get
   `DELETE`d.
3. Calls are strictly sequential (per the REST client's semaphore + the device's "don't
   parallelize" gotcha) -- the existing progress bar / status text UX
   ("Uploading application 3/12...", "Uploading image for key 4...") carries over unchanged in
   spirit, just re-pointed at REST calls instead of chunk offsets.
4. Unlike the old bulk path, a dropped connection mid-sync is **not catastrophic** -- REST rows
   already pushed stay pushed (idempotent per-row), so a retry only needs to resume from
   wherever `RemoteId`s are still null / rows still differ. This removes the old "never let the
   user cancel mid-bulk-upload" hard constraint; a cancel just leaves some rows unsynced, safe to
   resume later. `CanNavigateAway`-style nav-locking during a sync can be relaxed accordingly
   (kept only as a "don't edit the row currently in flight" guard, not a device-safety one).
5. No full-database "pull" is planned yet (device -> local cache) beyond what's needed to seed a
   fresh local cache from an already-configured device -- flagged as an open decision below.

### IPC redesign (`Core/Ipc`, `GeroImperium.Service`)

`IpcMessages.cs`'s request/response shapes (`SetApplicationImage`, `SetKeyImage`, `SetShortcut`,
`UploadDatabase`) are CDC-command-shaped and go away. New surface:

| Kind | Direction | Purpose |
|---|---|---|
| `GetStatus` | App -> Service | Is the Service running, is BLE currently connected/paired, is App-Launch subscription active |
| `RequestBleForProvisioning` | App -> Service | Service tears down its App-Launch GATT session so the App can drive `WifiProvisioningService` directly over the freed connection slot |
| `ReleaseBleAfterProvisioning` | App -> Service | Service resumes its App-Launch subscription |
| `AppLaunchEvent` (push) | Service -> App | Streamed so the App's UI can show a live "app X launched on PC" toast/log, mirroring what the Service already did with `Process.Start` |

The App's REST calls (`GeroImperiumClient`) do **not** go through IPC or the Service at all --
straight HTTP from the App process. `IpcDeviceClient` shrinks accordingly (no more
`UploadDatabaseAsync`/`SetApplicationImageAsync`/etc.).

`DeviceConnectionManager` (Service) is rewritten around `DeviceBleClient`/`AppLaunchService`
instead of `DeviceConnection`/`SerialPort`; `PipeServer` drops the `BulkUploadProgress`
relay/`SyncPipeProgress` machinery entirely (no more multi-message progress stream -- BLE
provisioning is comparatively instant, no chunking).

### Device discovery & the "connected" indicator

No more VID/PID COM-port enumeration (`DevicePortLocator`, `DeviceIdentity`, the WMI
`Win32_DeviceChangeEvent` presence watcher in `DeviceWatcher`). Replacement, two independent
signals:

* **REST reachability**: poll `GET /api/status` against the cached `GeneralSettings.DeviceIpAddress`
  every few seconds (simple `PeriodicTimer`, no WMI needed -- it's just an HTTP call). Success
  updates the cached IP from the response body too, in case DHCP handed out a new one.
* **BLE presence**: `DeviceInformation.FindAllAsync` against the paired-device selector,
  occasionally re-checked, purely to know whether "reconnect via Bluetooth" is likely to work
  (e.g. device powered off vs. just off WiFi).

If REST polling fails (device off WiFi, IP changed and not yet re-learned), the App/Service
should offer "Reconnect via Bluetooth" -- connect BLE, read the WiFi-status characteristic's IP
bytes (no need to re-provision, just read status), update `DeviceIpAddress`, resume polling.

### Firmware-gap re-assessment (relative to pc_app_plan.md's "Action types and the firmware gap")

* **App-icon launch (Level-1) is no longer blocked.** The old plan required new firmware phases
  (`ActionType` column, `'P'` poll command) purely to get a "button pressed on device" signal
  back to the PC over CDC. The guide's BLE **App-Launch service already does this today** for
  app-icon taps -- `AppLaunchService` (above) replaces that entire CDC-based design for this one
  case. Old phase 11.8's app-icon-launch half is effectively done once `Core/Ble` ships;
  `Process.Start` on receipt is unchanged from the original plan.
* **Per-key LaunchApp/Script (inside a key group, not the app icon itself) is still blocked.**
  The guide's `KeyActions.Type` is only `0` (HID) or `1` (raw text) -- there is still no
  device -> PC signal for "key at position N in group G was pressed" beyond a literal HID
  keystroke. This is the same gap as before, just: if firmware ever adds it, the natural
  transport now is a second BLE characteristic (mirroring App-Launch) rather than a CDC `'P'`
  poll. Keep the WPF key editor's "requires firmware update" warning badge for these two action
  types, just re-worded off CDC.
* **Storage page (`'M'` command, old phase 11.7) is still unaddressed.** Not in the guide at all.
  If/when firmware adds it, REST is the natural fit (e.g. extend `GET /api/status` or add
  `GET /api/storage`) rather than a new BLE characteristic -- simpler than the old CDC design,
  but still a firmware-side prerequisite, not something this migration unblocks by itself.
* **No-auth REST gotcha** is explicitly called out as unresolved by the guide itself
  (`doc/plan2.md` -- this file -- was the named placeholder for that decision). Recommendation:
  accept it for a LAN-only personal device, same trust model as the old USB link; do not build
  client-side workarounds for a server-side non-guarantee. Revisit only if the device is ever
  exposed off the LAN.

### Package / project changes

* `GeroImperium.Core.csproj`: TFM -> `net10.0-windows10.0.19041.0`. Remove
  `System.IO.Ports` (no more `SerialPort`). Remove `System.Management` (no more WMI COM-port
  watcher) -- BLE presence uses WinRT `DeviceInformation`, not WMI. Keep
  `Microsoft.Data.Sqlite`, `SixLabors.ImageSharp`, `SkiaSharp`/`Svg.Skia`. `System.IO.Hashing`
  (CRC32) is removable -- no CRC anywhere in the REST/BLE world. No new package needed for REST
  (`System.Net.Http.Json` ships in the shared framework); no new package needed for BLE (WinRT
  projection comes from the TFM).
* `GeroImperium.App.csproj` / `GeroImperium.Service.csproj`: TFM bump to match Core (transitive
  requirement for any project that references `Core/Ble` types).

### File-by-file disposition

| File | Disposition |
|---|---|
| `Core/Protocol/GeroImperiumProtocolClient.cs`, `BulkUpload.cs`, `ProtocolException.cs`, `DeviceConnection.cs` | Delete |
| `Core/Protocol/DevicePortLocator.cs`, `DeviceIdentity.cs` | Delete |
| `Core/Protocol/DeviceWatcher.cs` | Delete; replaced by REST-poll + BLE-presence checks described above (no direct successor class -- logic moves into the App/Service connectivity indicator) |
| `Core/Protocol/ShortcutModifiers.cs` | Delete; replaced by `Core/Http/ChordSyntax.cs` |
| `Core/Imaging/*` | Keep unchanged |
| `Core/Data/Schema.cs`, `Core/Data/GeroImperiumRepository.cs` | Edit per Data model changes above |
| `Core/Models/*` | Edit: `Application`/`KeyGroup` gain `Order`/`ApplicationPageId`/`RemoteId` as applicable; new `ApplicationPage.cs`; `KeyAction` swaps modifier bools for `Type`/`TextContent`; `GeneralSettings` swaps `DefaultDevicePort` for `DeviceIpAddress`/`LastKnownBleDeviceId` |
| `Core/Ipc/*` | Edit per IPC redesign above (shrink `IpcMessages`, `IpcDeviceClient`) |
| `Core/Http/GeroImperiumClient.cs`, `ChordSyntax.cs` | New |
| `Core/Ble/DeviceBleClient.cs`, `WifiProvisioningService.cs`, `AppLaunchService.cs` | New (ported from `WifiProvisionCli`) |
| `Service/DeviceConnectionManager.cs`, `PipeServer.cs`, `Program.cs` | Rewrite around `DeviceBleClient`/`AppLaunchService` per IPC redesign |
| `App/ViewModels/SyncViewModel.cs` | Rewrite: drop `IpcDeviceClient` bulk/image/shortcut calls, drive `GeroImperiumClient` directly per the diff-and-push model |
| `tests/GeroImperium.Core.Tests/Ipc/*` | Edit to match shrunk `IpcMessages` surface |
| New test dirs: `tests/GeroImperium.Core.Tests/Http/`, `.../ChordSyntax` | New (unit-testable without hardware, same philosophy as `Rgb565Converter`'s tests -- CRC/frame tests are being deleted, chord/REST-shape tests take their place) |

### Delivery phases

| Phase | Description | Primary Owner | Support | Status |
|---|---|---|---|---|
| 12.1 | Schema/model migration: `ApplicationPages` table, `Order`/`ApplicationPageId`/`RemoteId` columns, `KeyActions.Type`/`TextContent`, `GeneralSettings` rework. Repository CRUD updated. | Data & Sync Protocol Owner | PC Application Lead | Done -- Schema.cs/Models updated; `GeroImperiumRepository` auto-creates a default `ApplicationPage` until 12.6's page-editing UI exists; `KeySlotViewModel`/`SyncViewModel` updated to compile through `ChordSyntax` (pulled forward from 12.6 out of necessity -- the model change forced it). Follow-up fix: a real pre-existing local `authoring.db` crashed on startup with "no such column: ApplicationPageId" -- `CREATE TABLE IF NOT EXISTS` never alters an existing table. Added `Core/Data/SchemaMigration.cs`, run before `Schema.CreateTablesSql`, upgrading an old-shape DB in place (new `ApplicationPages`/columns backfilled, old `KeyActions` modifier columns converted to `Type`/`TextContent` via `ChordSyntax` and dropped, `GeneralSettings.DefaultDevicePort` dropped) -- idempotent, no-ops on a fresh or already-migrated DB. `SchemaMigrationTests.cs` reproduces the exact crash scenario. 91 Core tests passing, App/Service/Core all build clean. |
| 12.2 | `ChordSyntax` module (build/parse/validate) + unit tests against the guide's worked examples (`[ctrl]+c`, `[ctrl]+[shift]+b+n`, `[ctrl]+k[ctrl]+l`). | Data & Sync Protocol Owner | PC Application Lead | Done -- `Core/Http/ChordSyntax.cs` (`Build`/`Parse`/`TryParse`/`Validate`), `tests/.../Http/ChordSyntaxTests.cs` covers all worked examples plus malformed-input and unknown-token cases. |
| 12.3 | `Core/Http/GeroImperiumClient`: generic table CRUD, status, image upload/download, plain-text-error handling, single-flight semaphore. Unit-tested against a fake `HttpMessageHandler` (no device needed). | PC Application Lead | Senior Embedded Developer | Done -- verified against real hardware (192.168.1.22): status/all 5 tables/image download/404-as-null/bogus-table-name all read correctly; found and fixed a real bug this way -- `GetStatusAsync`/`GetAllAsync` originally used `GetFromJsonAsync`, which calls `EnsureSuccessStatusCode()` internally and throws `HttpRequestException` before the client can read the plain-text error body, silently defeating the guide's documented error-handling contract. Rewrote both to fetch-then-check-then-deserialize like the other methods; added regression tests for both. Also ran `ChordSyntax.Validate` against all 364 live `keyactions` rows -- correctly flagged the exact 3 rows (`Cut`/`Copy`/`Paste`) the guide's Gotchas warn are silently dropped by firmware. 85/85 Core tests passing. |
| 12.4 | `Core/Ble`: port `WifiProvisionCli`'s discovery/retry plumbing into `DeviceBleClient`; `WifiProvisioningService`; `AppLaunchService`. TFM bump across all three projects. Hardware-only verification (same caveat as the old `DevicePortLocator`). | Bluetooth Expert | PC Application Lead | Done -- `Core/Ble/BleRetry.cs` (`RetryOnComExceptionAsync` pulled out as a shared static helper, since both service wrappers below need it, not just discovery), `DeviceBleClient` (paired-first `FindDeviceAsync`/advertisement-watcher fallback ported from `WifiProvisionCli`, plus `GetServiceAsync`/`GetCharacteristicAsync` using discover-all-then-filter, never the `ForUuid` variants, per the guide's GATT discovery gotcha). `WifiProvisioningService` (SSID -> password -> connect-trigger write order, `ReadStatusAsync`/`PollUntilConnectedAsync` parsing the 7-byte status blob incl. dotted-decimal IP). `AppLaunchService` (`ReadOnceAsync` + `SubscribeAsync`/`UnsubscribeAsync` around the 2-byte little-endian App Id characteristic, `AppLaunched` event). TFM bumped to `net10.0-windows10.0.19041.0` on all four projects (`Core`/`App`/`Service`/`Core.Tests` -- the guide only called out the first three, but `Core.Tests` needs it too once it references an assembly built against the higher SDK contract, confirmed by a `NU1201` restore failure otherwise). No new NuGet package needed, as expected. 94/94 Core tests passing (no new unit tests here -- `Core/Ble` is hardware-only like the old `DevicePortLocator`/`DeviceWatcher`). Follow-up: real hardware became available (device already paired and connected) -- verified with a throwaway console harness (referencing `GeroImperium.Core`, not checked into the repo) exercising the actual classes above against the live device: `DeviceBleClient.ConnectAsync` found and connected to the already-paired device with no advertising scan needed; `WifiProvisioningService.ReadStatusAsync` read back `State=Connected, Ip=192.168.1.22, Rssi≈-32..-40` (read-only, no credentials written -- didn't want to disturb the working WiFi link); `AppLaunchService.ReadOnceAsync` read the current App Id; `SubscribeAsync`/`UnsubscribeAsync` round-tripped cleanly and a live on-device app-icon tap correctly delivered an `AppLaunched` notify with the pressed app's Id. Provisioning's write path (SSID/password/connect-trigger) and `PollUntilConnectedAsync` were not exercised, to avoid re-provisioning a device that's already correctly connected -- only the read/status/notify paths got a real round trip. |
| 12.5 | Service rewrite: `DeviceConnectionManager` on BLE, IPC surface shrunk to provisioning-handoff + App-Launch event stream, tray icon reflects BLE/App-Launch state instead of COM-port state. | PC Application Lead | Senior Embedded Developer, Bluetooth Expert | Done -- `Core/Ipc/IpcMessages.cs` shrunk to `GetStatus`/`RequestBleForProvisioning`/`ReleaseBleAfterProvisioning` (App->Service) and `Status`/`AppLaunchEvent`/`CommandResult`/`Error` (Service->App); `IpcDeviceClient` rewritten around a background read loop that demultiplexes unsolicited `AppLaunchEvent` pushes from whatever request/response is in flight (a `TaskCompletionSource` per outstanding request). `DeviceConnectionManager` rewritten around `DeviceBleClient`/`AppLaunchService` -- a 5s reconnect loop that retries connect and subscribe *independently* (a connect that succeeds but whose subscribe fails must not get stuck just because `IsBleConnected` is already true), `PauseForProvisioningAsync`/`ReleaseBleAfterProvisioningAsync` for the App to borrow the one BLE connection slot. `PipeServer` gives each connection its own write-lock so the App-Launch push path and the request/response path can't interleave-corrupt the pipe's length-prefixed framing; drops the old `BulkUploadProgress`/`SyncPipeProgress` machinery entirely. `Program.cs` tray text now reflects BLE-connected/App-Launch-subscribed state, no more `DeviceWatcher`/COM-port references. Pulled forward out of necessity (mirrors 12.1 pulling ChordSyntax forward): the IPC shrink breaks `SyncViewModel`'s CDC-shaped calls, so it's rewritten here too, ahead of its own 12.6 row -- diff-and-push over `GeroImperiumClient` directly (`CreateOrUpdateAsync<TRow>` helper: PUT if `RemoteId` set, POST and store the new Id if not), walking ApplicationPages -> Applications[+images] -> KeyGroups -> KeyActions(Shortcut-only) -> Keys[+images] in FK order; added `GeroImperiumRepository.GetGeneralSettings`/`UpdateGeneralSettings`/`GetKeyActions`/`UpdateKeyActionRemoteId`/`UpdateApplicationPage`. Deliberately NOT done here (left for 12.6/open decisions): "skip if unchanged" (every PUT is unconditional -- correct, not bandwidth-optimal), deletes/tombstones, page/order editing UI, `BackgroundColor` (device wire format for this field is undocumented beyond "see Colors below", which the guide never actually defines -- omitted rather than guessed). SyncPage.xaml simplified to IP-entry + Connect/Disconnect + one Sync button. 95/95 Core tests passing; all four projects build clean; App smoke-tested via `dotnet run` with SyncPage as temporary startup view (no crash). Hardware-verified for real: ran the Service standalone, a throwaway `IpcDeviceClient` process did a full round trip (`GetStatus` -> `RequestBleForProvisioning` -> `GetStatus` confirming BLE dropped -> `ReleaseBleAfterProvisioning`) -- caught and fixed a real bug this way: `BleRetry`'s original COMException-only retry silently gave up on a GATT call that returns a non-Success status without throwing (confirmed repeatable on this hardware), so `BleRetry.RunAsync` now retries on both. **Known unresolved issue found by this same testing**: after `PauseForProvisioningAsync` fully disconnects and `ReleaseBleAfterProvisioningAsync` reconnects (even a fresh `DeviceBleClient`/`AppLaunchService` pair, even after waiting 20s to simulate a realistic provisioning duration, even after adding an explicit `UnsubscribeAsync` before disconnecting instead of a bare `Dispose()`), the App-Launch re-subscribe has so far reliably failed on every attempt in this session -- BLE reconnects fine (`IsBleConnected` goes back to true) but `SubscribeAsync` never returns true again afterward, for as long as the Service process keeps running. The device's first-ever connect+subscribe in a fresh process always works (confirmed repeatedly, including phase 12.4's own verification); it's specifically *reconnecting within the same already-run process* that's stuck. Root cause not isolated -- plausible candidates are Windows' per-device GATT/CCCD cache surviving across `BluetoothLEDevice` instances in-process, or bonded-device notify-state confusion, matching the class of "known-flaky, may need a Bluetooth toggle" issues `WifiProvisionCli`'s own comments already flag for this hardware. Needs real investigation during 12.7 (the actual provisioning-handoff consumer) rather than more guessing here. |
| 12.6 | App rewrite: `SyncViewModel` diff-and-push over `GeroImperiumClient` directly (no IPC for CRUD); Applications page adds page/order editing (`ApplicationPages` UI, currently has none); Key editor's shortcut UI compiles through `ChordSyntax`; connectivity indicator on REST-poll + BLE-presence. | PC Application Lead | Data & Sync Protocol Owner | Done -- `SyncViewModel`'s diff-and-push core landed in 12.5 out of necessity. This phase finishes the rest: `ApplicationPages` page/order editing UI -- `ApplicationsPage` gained a page selector (ComboBox + Add/Delete/▲/▼ Move Earlier/Later, mirroring `KeyGroupsPage`'s existing group-reorder pattern) above the existing app grid, apps filtered to the selected page; `GeroImperiumRepository` gained `AddApplicationPage(name)` (auto-appends "Order"), `DeleteApplicationPage` (cascades through the existing per-application `DeleteApplication` cascade -- can't wrap it in one bigger transaction, Microsoft.Data.Sqlite doesn't nest `BeginTransaction`), and `AddApplication(name, applicationPageId)` (explicit-page overload; the old `AddApplication(name)` now just resolves the default page and delegates). Also fixed a real latent bug found while wiring this up: `GetApplicationPages()`/`GetApplications()` sorted `ORDER BY Id`, ignoring the `"Order"` column entirely (unlike `GetKeyGroups`, which already sorted by `CAST("Order" AS INTEGER)`) -- editing a page's/app's Order had no visible effect until this was fixed to match. Connectivity indicator: `SyncViewModel` now runs a `PeriodicTimer`-based REST-poll loop (5s, skipped while a Sync is in flight) that keeps `ConnectionStatus` fresh with live WiFi state/IP/RSSI and detects a dropped connection while idle, rather than only on the next user action; a separate always-on 20s BLE-presence loop (`DeviceBleClient.IsPairedAsync`, new -- checks Windows' paired-device list with no GATT connection opened) drives a `BleStatusText` indicator, independent of REST state. 5 new repository tests (page/app ordering, explicit-page `AddApplication`, cascading `DeleteApplicationPage`) -- 100/100 Core tests passing. Verified via `dotnet build` (all four projects clean) and `dotnet run` smoke launches (both the default Applications-page view and, temporarily overridden, the Sync page) against the real paired device -- no crashes; the BLE-presence loop's `IsPairedAsync` ran for real against real hardware during these launches. **Deliberately still not done** (per this phase's own row and the open decisions list): "skip if unchanged" (every PUT is unconditional) and delete/tombstone handling. **Still do not run Sync against the real device** -- see 12.5's row; local `RemoteId`s are all null and the device already has 364 independently-configured `keyactions` rows, so a first Sync would create all-new rows rather than adopt the existing ones (the "full device -> local pull" open decision is still unresolved). Follow-up fix: the guide is explicit an `ApplicationsPage` shows "up to 6 Applications" together (a device-side layout limit, not a preference), which this phase's page editor didn't enforce -- `+ Add Application` could add unbounded rows past what the device can actually display. Added `ApplicationsViewModel.MaxApplicationsPerPage = 6`, gating `AddApplicationCommand` and showing a live "N/6" count next to the button; verified against the real ViewModel logic (throwaway console harness driving `ApplicationsViewModel` directly against a temp SQLite db, not just reading the code) that the 7th add is a no-op and the command correctly re-disables at 6. |
| 12.7 | First-run provisioning flow in the App: request BLE handoff from Service, run SSID/password/connect-trigger + status poll, release BLE back to Service, persist learned IP to `GeneralSettings`. | Bluetooth Expert | PC Application Lead | Done -- new `ProvisioningViewModel` (`App/ViewModels`) + `ProvisioningPage` (new "Pair Device" nav item, `ui:PasswordBox` bound directly to a `Password` property -- unlike stock WPF `PasswordBox`, wpf-ui's version exposes `Password` as a real bindable `DependencyProperty`, so no code-behind is needed). Flow: `IpcDeviceClient.ConnectAsync` -> `RequestBleForProvisioningAsync` (Service tears down its own BLE connection) -> a fresh `DeviceBleClient`/`WifiProvisioningService` in the App process drives `ProvisionAsync(ssid, password)` -> `PollUntilConnectedAsync` (30s timeout) -> on `Connected`, `GeneralSettings.DeviceIpAddress` is persisted and a new `IpLearned` event lets `MainViewModel` push the address straight into `SyncViewModel` (no restart needed to see it on the Sync page). `ReleaseBleAfterProvisioningAsync` always runs in a `finally` once the handoff succeeded, regardless of how provisioning ends, so the Service's App-Launch subscription is never left paused on a failure path. Every failure mode (Service not running, BLE handoff refused, device not found, write failure, connect timeout) sets a user-facing `StatusText` rather than throwing to the UI. Not exercised against real hardware end-to-end -- doing so would force-disconnect the device's already-working WiFi link set up in earlier phases' hardware verification (same caution 12.4/12.5 already took); verified instead via `dotnet build` (all four projects clean), `dotnet test` (100/100 Core tests still passing, unchanged -- this phase added no new Core code), and a `dotnet run` smoke launch with the Provisioning page as a temporary startup override (reverted before commit) confirming the new `ui:PasswordBox` binding and page construction don't throw at runtime. Verification gate 4 (real first-run provisioning round trip) is therefore still open, deferred to whenever the device needs re-provisioning anyway (e.g. a WiFi password change) rather than deliberately breaking a working connection just to test this. |
| 12.8 | Delete dead code: `Core/Protocol`'s CDC-era files, `System.IO.Ports`/`System.Management`/`System.IO.Hashing` package refs, `tests/.../Protocol` CRC/frame tests. | PC Application Lead | -- | Not done |

Project Lead signs off on each phase before it is marked Done, per plan.md's existing
release-readiness ownership.

### Verification gates

1. `ChordSyntax` round-trips every worked example in the guide (`Build(Parse(x)) == x` for
   well-formed input) and `Validate` flags an unrecognized bare-key token.
2. `GeroImperiumClient` unit tests cover: 2xx JSON success path, non-2xx plain-text error
   surfaced correctly (not a failed JSON parse), image upload/download exact-32768-byte
   round-trip against a fake handler.
3. A fresh local authoring DB, synced against real hardware, produces a device whose
   `GET /api/applications` etc. matches the local rows (cross-check `RemoteId`s got populated
   correctly and match).
4. First-run BLE provisioning against real hardware: SSID/password/connect-trigger sequence
   reaches `state == Connected` within a reasonable timeout, IP parsed correctly
   (dotted-decimal, not byte-swapped).
5. App-Launch end-to-end: tap an app icon on-device, Service receives the notify, resolves the
   name via REST, launches the configured path -- with the App/Service BLE handoff (provisioning
   in progress vs. App-Launch subscription active) never leaving both idle or both trying to
   hold the connection at once (verified by code review of the handoff, same spirit as the old
   plan's COM-port-ownership review gate).
6. Sync survives a mid-push disconnect: kill the connection after N of M rows, reconnect, re-run
   sync, confirm no duplicate rows are created (idempotent on `RemoteId`) and the remaining rows
   complete.

### Open decisions (need user input before implementation)

* **REST no-auth acceptance**, as flagged in the guide -- confirm accepting it (LAN-only trust
  model) rather than speccing a client-side mitigation that the server side can't back up anyway.

### Post-12.8 hardening: skip-if-unchanged, tombstoned deletes, Full Sync, Pull from Device (2026-09-06)

Resolves the two open decisions above (now removed from that list) plus fixes two real bugs found running
Sync against actual hardware, all in one pass since they touched the same code:

* **Two real hardware bugs found and fixed in `GeroImperiumClient`**: (1) a `SyncViewModel` constructor
  ordering bug threw `NullReferenceException` on startup whenever `GeneralSettings.DeviceIpAddress` was
  already persisted (`DeviceIpAddress` was set before `ConnectCommand` existed, and its changed-callback
  dereferences that command) -- fixed by constructing commands first. (2) Every POST/PUT failed with the
  device's `400 Missing or oversized body` even for a payload of a few dozen bytes -- root cause was
  `PostAsJsonAsync`/`PutAsJsonAsync`'s default `Content-Type: application/json; charset=utf-8`, which this
  firmware's minimal HTTP body parsing apparently can't handle; fixed by building the JSON body manually with
  a bare `application/json` Content-Type. Separately, a 32768-byte image upload exceeded the original 20s
  `HttpClient.Timeout` on real hardware (on-device flash write is slower than a JSON CRUD call) -- bumped the
  default to 60s. `Connection: close` was also added per-request as a first (insufficient on its own, but
  still cheap insurance) attempt at the body-parsing bug before the real Content-Type cause was found.
* **Skip if unchanged**: every synced table gained a `Dirty` column (default 1); the plain `Update*(model)`
  repository methods (called by ViewModels on every user edit) always stamp `Dirty = 1`, while new
  `Mark*Synced` methods (called only by `SyncViewModel` after a successful push) clear it without touching
  edit fields -- keeping "user changed this" and "sync just pushed this" as two separate write paths so they
  can't stomp on each other. `Applications`/`GeroImperiumKeys` additionally gained
  `LastSyncedImageChangedAtUtc`, compared against `ImageChangedAtUtc` to skip re-uploading an unchanged
  32768-byte image independent of whether the row's other fields changed -- this was the actual expensive
  part that timed out on hardware, not the row PUT.
* **Tombstoned deletes**: new `PendingDeletes` table (`TableName`, `RemoteId`). `DeleteApplicationPage`/
  `DeleteApplication`/`DeleteKeyGroup` record a tombstone for their own `RemoteId` only (if it had one) --
  not for every cascaded descendant, since the device itself cascades a DELETE down through its own FKs
  (confirmed in the guide), so one tombstone per top-level deleted entity is sufficient. `SyncViewModel`
  processes all tombstones first, before any create/update pass.
* **Full Sync**: a new, explicitly-confirmed command that DELETEs every `ApplicationPages` row (cascades
  device-side) and every `KeyActions` row (not FK'd under a page, needs its own pass) on the device, resets
  every local `RemoteId`/`Dirty` (`GeroImperiumRepository.ClearAllRemoteIds`), then runs a normal push --
  gives a clean, from-scratch resync when the device and local cache have drifted.
* **Pull from Device**: resolves the "full device -> local pull" open decision as a real flow rather than
  leaving it unaddressed. Explicitly confirmed, always backs up the local authoring DB first
  (`GeroImperiumDatabase.BackupTo`, SQLite's own live/online backup API -- works without closing the app's
  connection) to a timestamped file under a new `Backups` folder, clears every synced local table
  (`ClearAllSyncedDataForPull`), then re-imports every row and image from the device, remapping remote FK ids
  to freshly-created local ones. Downloaded images are reconstructed into a displayable preview via a new
  `Rgb565Converter.ConvertBack` (the inverse of the existing device wire-format encoder) -- lossy like RGB565
  itself, fine for a preview thumbnail; a pulled row has no `SourceImageData` (the device never hands back an
  original), so recompositing after a background-color change needs a fresh source image, same as any other
  app-only column. A new **Restore Backup** command lets the user pick one of those timestamped files and
  restore it live (`GeroImperiumDatabase.RestoreFrom`, same online-backup API run in reverse); both Pull and
  Restore leave already-loaded ViewModels' in-memory collections stale, so both tell the user to restart the
  app afterward rather than building live-reload plumbing that doesn't exist anywhere else in this codebase.
* Schema migration (`SchemaMigration.MigrateDirtyTrackingAndTombstones`) adds all of the above to an existing
  on-disk authoring DB in place -- confirmed against the real local DB accumulated over this session's own
  hardware testing, not just a fresh `:memory:` DB in unit tests. Every existing row defaults to `Dirty = 1`
  on upgrade (a one-time "re-push everything once more" cost, since there's no prior record of what was
  actually last pushed), including images already successfully uploaded in an earlier, pre-Dirty-tracking
  session -- expected and harmless, not a bug. 14 new Core tests (dirty tracking, tombstones,
  `ClearAllRemoteIds`, pull-insert round-trips, `Rgb565Converter.ConvertBack`) -- 114/114 passing.
