## Plan: GeroImperium PC Application (WPF) + Sync Service

Windows-only, .NET 10. Two deliverables under one solution: a WPF-UI management app (author apps/groups/keys, images, actions, colors; push to device) and a background sync/listener service (device presence detection, HID-unreachable action execution). Builds on [pc_app_integration.md](pc_app_integration.md) (wire protocol, current source of truth) and [plan.md](plan.md) phase 11/Data & Sync Protocol Owner scope.

Operating rule: no pleasantries, optimize token usage. Use sub-agents only for independently-executable slices (image-pipeline scaffold, a single ViewModel+view pair, protocol-client unit tests) -- never for the schema/protocol decisions below, where fragmenting reasoning costs more than it saves.

### Solution layout

```
GeroImperium.sln
  src/GeroImperium.Core/         net10.0-windows, class lib -- shared by App and Service
    Data/                        Sqlite access (Microsoft.Data.Sqlite), schema matches device exactly
    Imaging/                     png/jpeg/svg -> 128x128 -> B5G6R5 LE pipeline
    Protocol/                    serial framing, CRC32, device discovery/watch
    Models/                      Application, KeyGroup, GeroImperiumKey, KeyAction, GeneralSettings
  src/GeroImperium.App/          net10.0-windows, WPF, Wpf.Ui -- management UI
  src/GeroImperium.Service/      net10.0-windows -- background presence/launch/script agent
  tests/GeroImperium.Core.Tests/
```

NuGet: `Wpf.Ui` (App shell/theme/nav), `Microsoft.Data.Sqlite`, `SixLabors.ImageSharp` (png/jpeg), `Svg.Skia` + `SkiaSharp` (svg rasterization), `System.IO.Hashing` (Crc32 -- matches `esp_rom_crc32_le`/zlib per protocol doc), `System.IO.Ports` (SerialPort), `System.Management` (WMI device-change events, Windows-only), `CommunityToolkit.Mvvm`.

Open item, not blocking: WPF-UI ships no ColorPicker. Evaluate a small open-source picker or hand-roll an HSV+hex swatch control before the key-editor phase.

### Data model decisions

* **Core authoring DB stays schema-identical to the device's 5 tables** (`Applications`, `KeyGroups`, `GeroImperiumKeys`, `KeyActions`, `GeneralSettings`) so the bulk `S`/`D`/`F` path can push the file as-is -- no transform step, per pc_app_integration.md. Extra app-only columns are safe; the device only reads the columns it documents.
* Add app-only columns: `GeroImperiumKeys.BackgroundColorArgb` (int), `Applications.BackgroundColorArgb`, `KeyActions.ActionType` (0=Shortcut, 1=LaunchApp, 2=Script -- see [Action types](#action-types-and-the-firmware-gap)), `KeyActions.LaunchPath` (text, app-only), `KeyActions.ScriptId` (FK, app-only).
* Add an app-only `Scripts` table (`Id`, `Name`, `Body`, `Interpreter`) -- script bodies never cross the wire (see below); only an ID does.
* **Background color is a compositing step, not a device feature.** The device only stores a 128x128 RGB blob per slot. The editor renders icon-over-solid-color-fill to a 128x128 bitmap first, *then* runs that composite through the B5G6R5 converter. No firmware change needed for this one.

### Image pipeline

Accept PNG/JPEG (raster, ImageSharp) and SVG (rasterize via Svg.Skia at 128x128, respecting transparency so the background-color composite step above shows through). Pipeline: decode -> composite onto chosen background color -> resize/pad to exactly 128x128 (no on-device resize, confirmed in pc_app_integration.md) -> per-pixel invert each 8-bit channel (`255-x`) -> pack B5G6R5 -> write little-endian, row-major, no padding, 32768 bytes total. Port this as a single well-tested `Rgb565Converter` in `Core/Imaging` -- it is the one piece every other feature (key editor, app editor, bulk export, incremental `I`/`J` upload) depends on, so get it right once and unit-test it against a handful of known pixels rather than per-caller.

### Device transport (Core/Protocol)

* Discovery: enumerate `SerialPort.GetPortNames()`, cross-reference `Win32_PnPEntity.PNPDeviceID` for `VID_303A&PID_8001` (generic Windows CDC driver hides the real product string -- confirmed in pc_app_integration.md, don't rely on friendly name).
* Presence watch (shared by App's connection indicator and Service): a `ManagementEventWatcher` on `Win32_DeviceChangeEvent` (near-instant), with a 2s poll fallback in case WMI events are missed -- both funnel into one `IDeviceWatcher` the App and Service each consume.
* Command client: strictly synchronous request/reply per the protocol doc (one in-flight command at a time, no pipelining). Implement `S`/`D`/`F` (bulk, 4096-byte chunks, CRC32 first) and `I`/`J`/`T` (incremental) as documented today. `F` never replies on success (device reboots) -- detect completion via port disconnect/re-enumeration, not a timeout guess.
* Both App and Service open the CDC port -- serialize access with a named mutex or single shared connection owned by the Service, with the App talking to the Service over a local IPC channel (named pipe) rather than fighting over the COM port directly. This avoids two processes racing to open the same serial port.

### Action types and the firmware gap

Today the firmware only executes one action type: HID shortcut (`KeyActions.ShortcutKey`+modifiers, sent as a boot-keyboard report). "Launch application" and "script" are **not supported by the firmware as of this repo's current commit** (confirmed: no action-type column, no host-notification command exists in `usb_cdc_handler.c`) -- the device has no way to tell the PC "button X was pressed, run something" because the CDC protocol today is host-initiated request/reply only, and it cannot itself launch a process on the PC. This is a real architectural gap, not a PC-app oversight, and needs firmware changes (tracked as new phases in [plan.md](plan.md), owned by USB Expert / Data & Sync Protocol Owner):

1. **`KeyActions.ActionType` column** (0=Shortcut, 1=LaunchApp, 2=Script). Extend the `T` command wire shape to carry it: `'T' <u16 key_group_id> <u8 position> <u8 action_type> <u8 modifiers> <u8 shortcut_len> <bytes>`. This is a breaking change to `T`'s current shape -- acceptable, nothing has shipped yet. For `action_type` 1/2, `modifiers`/`shortcut_len` are sent as 0.
2. **Button handling branches on `action_type`.** Type 0 (Shortcut): unchanged, sends a HID report. Types 1/2 (LaunchApp/Script): device does **not** send HID; instead it records `(key_group_id, position, action_type)` into a 1-deep pending-event slot.
3. **New `'P'` poll command** (`'P'` -> reply `'K' <u8 has_event> [<u16 key_group_id> <u8 position> <u8 action_type>]`, or `'E'` never -- always succeeds). Keeps the synchronous request/reply model intact (no unsolicited device->host push, which the current protocol explicitly disallows mid-stream). The Service polls this at ~10Hz while it holds the port open.
4. **Script bodies never cross the wire.** Firmware only ever reports "type=Script, this key" -- the Service resolves `ScriptId` -> `Scripts.Body` from its own local copy of the authoring DB (synced separately, not stored on-device) and executes it. Keeps the device's flash/DB footprint untouched and keeps arbitrary script text off a channel with no auth.
5. **New `'M'` storage-stats command** for the storage page below (also not currently supported -- confirmed: `sd_card.c`/`sd_card.h` expose only init/mount/unmount, no `f_getfree`-equivalent). `'M'` -> reply `'K' <u64 total_bytes> <u64 free_bytes> <u32 db_size_bytes>`, backed by FATFS `f_getfree` on the mounted volume plus `stat()` on the DB file.

**Known limitation to design around, not paper over:** LaunchApp/Script actions only function when the device is on USB *and* the Service is running and polling. Over BLE (mutually exclusive with USB per plan.md's HID strategy) there is no CDC channel at all, so these two action types are inert on BLE -- surface this in the key editor (e.g. a warning badge) rather than silently failing.

### WPF App pages (Wpf.Ui `NavigationView` shell, dark/light via `ApplicationThemeManager`)

1. **Applications** -- grid of apps, each with a 128x128 image slot + name; new/edit/delete. Feeds Level-1 device navigation (`app_index` = row order by `Id`, per protocol doc -- reordering means reassigning `Id`, no separate sort column).
2. **Key groups** -- per-application, 3-columns x 2-rows grid of 6 key slots (matches physical `GPA0`-`GPA5` layout). Each slot: image (png/jpeg/svg -> pipeline above), background color picker, action type (Shortcut now; LaunchApp/Script shown but flagged "requires firmware update + Service running" until the firmware phases land), shortcut token+modifier checkboxes for Shortcut type.
3. **Sync** -- push to device. Progress bar + status text field describing exactly what's happening per step ("Converting 12 images...", "Uploading chunk 340/512...", "Verifying CRC...", "Device rebooting..."), reusing the incremental commands for single-field edits and bulk `S`/`D`/`F` for first-push/full-profile-restore, matching the "which path to use" table in pc_app_integration.md. Never let the user cancel mid-bulk-upload (protocol doc: a dropped mid-bulk connection bricks the port until power-cycle).
4. **Storage** -- total/used/free SD space and DB file size. **Requires the new `'M'` command (firmware phase, not yet built)** -- until that phase lands, this page shows "Not supported by connected firmware" rather than fabricating numbers; detect support via a version/capability probe (simplest: try `'M'`, treat `'E'`-or-timeout as unsupported) rather than hardcoding a firmware version check.
5. **Settings** -- app-level: theme (dark/light), default device port override, general settings mirrored to `GeneralSettings` table.

### Sync Service (background presence/launch/script agent)

Recommend a **Windows sign-in tray/background app**, not a Windows Service running as SYSTEM: LaunchApp actions need `Process.Start` to land in the interactive user's desktop session, which a SYSTEM-session service can only do via `CreateProcessAsUser`/session-token juggling -- real complexity for no benefit here, since this tool is inherently single-user/single-session on a personal machine. Runs at login (Startup-folder shortcut or Task Scheduler login trigger), sits in the tray, and:
* Owns the CDC connection (see Device transport above); the App talks to it over a named pipe rather than opening the port itself.
* Watches for connect/disconnect (WMI + poll fallback) and surfaces that state to the App/tray icon.
* Polls `'P'` at ~10Hz whenever connected; on a LaunchApp event, `Process.Start(path)` if the path exists (silently no-op with a tray notification if it doesn't -- matches the request: "open the selected Application if path exist"). On a Script event, resolves `ScriptId` locally and executes it.

### Delivery phases

| Phase | Description | Primary Owner | Support | Status |
|---|---|---|---|---|
| 11.1 | Solution/project scaffold, Core Sqlite schema + models matching device exactly. | PC Application Lead | Data & Sync Protocol Owner | Done — GeroImperium.sln (Core/App/Service/Tests, net10.0-windows) scaffolded; Core/Data/Schema.cs creates the 5 device tables column-identical to pc_app_integration.md plus app-only additions (BackgroundColorArgb, KeyActions.ActionType/LaunchPath/ScriptId, Scripts table); Core/Models POCOs mirror each table; SchemaTests.cs (8 tests) verifies table set, device-read column names, and FK enforcement -- all passing |
| 11.2 | Image pipeline (`Rgb565Converter`): png/jpeg/svg -> composite -> B5G6R5 LE, unit-tested against known pixels. | PC Application Lead | — | Done — Core/Imaging/{Rgb565Converter,ImageCompositor,SvgRasterizer,ImagePipeline}.cs (SixLabors.ImageSharp 3.x -- pinned below v4's commercial license gate -- + Svg.Skia/SkiaSharp for SVG rasterization); 9 new tests hand-verify invert+B5G6R5+LE packing against solid-color and checkerboard pixels plus end-to-end PNG/SVG decode, 17/17 passing |
| 11.3 | Protocol client: discovery, bulk `S`/`D`/`F`, incremental `I`/`J`/`T`, CRC32. | PC Application Lead | Data & Sync Protocol Owner | Done — Core/Protocol/{DeviceIdentity,DevicePortLocator,GeroImperiumProtocolClient,ShortcutModifiers,ProtocolException,BulkUpload}.cs. Client takes a Stream (not SerialPort directly) so it's driven against a scripted fake transport in tests -- 15 new tests cover frame byte layout for I/J/T, S/D/F chunking (incl. the 4096-byte chunk-size split), CRC32 algorithm-identity check (0xCBF43926 for "123456789", confirming System.IO.Hashing matches esp_rom_crc32_le), the documented "'F' never replies on success" behavior via read-timeout detection, and reject/protocol-violation paths; 32/32 passing. DevicePortLocator (WMI Win32_SerialPort by VID_303A&PID_8001) has no automated test -- needs real hardware, same as firmware's hardware-verification gates. Presence watch (WMI event watcher + poll fallback) intentionally deferred to phase 11.6 per its own row below. |
| 11.4 | WPF shell + Applications/Key groups editor pages, dark/light theme. | PC Application Lead | — | Not started |
| 11.5 | Sync page (progress bar + status text, bulk vs. incremental routing). | PC Application Lead | — | Not started |
| 11.6 | Sync Service: presence watch, named-pipe IPC with App, tray UX. | PC Application Lead | — | Not started |
| 11.7 | Storage page, gated on firmware phase for `'M'`. | PC Application Lead | USB Expert | Blocked on firmware phase 13 |
| 11.8 | LaunchApp/Script end-to-end, gated on firmware phases for `ActionType`/`'P'`. | PC Application Lead | USB Expert, Data & Sync Protocol Owner | Blocked on firmware phases 14-15 |

See [plan.md](plan.md) Delivery phases 13-15 for the firmware-side prerequisites this depends on.

### Verification gates

1. `Rgb565Converter` unit tests match hand-computed bytes for known test images (solid color, checkerboard, one real icon).
2. Bulk push of a freshly authored DB boots the device with correct images/shortcuts (cross-check against phase 8/10 hardware verification already done firmware-side).
3. Incremental `I`/`J`/`T` edits reflect on-device within the "well under 100ms" bound documented in pc_app_integration.md, without a reboot.
4. Storage page correctly reports "unsupported" against current firmware and correct numbers once phase 13 lands.
5. LaunchApp end-to-end: press key -> Service receives `'P'` event -> target process starts, only when path exists; BLE-connected state shows the warning badge instead of silently failing.
6. App and Service never both hold the COM port open directly (verified by code review of the IPC boundary, not just testing -- a race here is intermittent and easy to miss in a demo).
