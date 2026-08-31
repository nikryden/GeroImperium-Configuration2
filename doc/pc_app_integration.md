# PC Application Integration Guide

Reference for the PC management application (doc/plan.md Delivery phase 11): how to talk to the firmware over USB, what the database looks like, and which update path to use when.

## Connectivity

The device enumerates as a composite TinyUSB device (`main/usb_device.c`) exposing:
- A CDC-ACM serial port -- this is the channel everything in this document rides on.
- A USB HID boot keyboard interface -- not used by the PC app; this is how the device sends keystrokes when USB is the active HID transport (irrelevant to management traffic).

Open the CDC-ACM port like any serial device (any baud rate setting is ignored -- it's a USB CDC virtual port, not a real UART). There is no handshake beyond the protocol itself: send a command byte, get a reply byte (`'K'` success / `'E'` error), one command at a time. The protocol is strictly synchronous request/reply -- do not pipeline a second command before the reply to the first arrives.

**Finding the right COM port**: don't match on the Device Manager friendly name -- Windows' inbox CDC-ACM driver (`usbser.sys`) always labels the port generically (e.g. "USB Serial Device" / localized "Seriell USB-enhet") and shows "Microsoft" as the manufacturer, regardless of the device's own `iManufacturer`/`iProduct` USB string descriptors (which are set to `GeroImperium`/`GeroImperium` in `usb_string_descriptor`, `main/usb_device.c`, but are not surfaced there -- that's a Windows generic-driver limitation, not something fixable from the firmware side without shipping a custom signed INF). Match on the USB **hardware ID** instead, which Windows does expose per-port and which every serial port's registry/PnP entry carries:
- VID `0x303A` (Espressif; `CONFIG_TINYUSB_DESC_USE_ESPRESSIF_VID`), PID `0x8001` (fixed, `CONFIG_TINYUSB_DESC_CUSTOM_PID` in `sdkconfig.defaults` -- chosen instead of the class-derived default `0x4005` specifically so it doesn't collide with other Espressif boards enabling the same CDC+HID class combo).
- On Windows: enumerate `SerialPort.GetPortNames()`/`Win32_PnPEntity`, then filter by `PNPDeviceID` containing `VID_303A&PID_8001` (e.g. via `Win32_PnPEntity.PNPDeviceID` or the registry under `HKLM\SYSTEM\CurrentControlSet\Enum\USB\VID_303A&PID_8001\...\Device Parameters\PortName`).

While any DB upload (bulk or incremental) is in progress, the device suspends HID reporting (`connectivity_suspend_hid`) and, for the bulk path, shows upload progress on display 0 and the LEDs (`main/usb_cdc_handler.c`'s `update_upload_progress`). Normal operation resumes automatically once the command completes (or, for a failed/incomplete bulk upload, once `usb_cdc_finalize_db_upload` returns an error).

All multi-byte integers are little-endian.

## Two update paths -- which one to use

| | Bulk (`S`/`D`/`F`) | Incremental (`I`/`J`/`T`) |
|---|---|---|
| Scope | Whole-file DB replace | One Applications image, one key image, or one key's shortcut |
| Device restarts? | Yes (`esp_restart()`) | No |
| Use for | First flash, factory reset, restoring a full profile | Live editing in the PC app -- one field changed, see it reflected without a reboot |

Default to incremental commands for anything the PC app does interactively. Reserve the bulk path for pushing an entirely new/restored database file.

## Bulk upload: `S` / `D` / `F`

1. **Start** -- `'S' <u32 size> <u32 crc32>` (CRC-32 of the whole file, computed with `esp_rom_crc32_le` -- confirmed the same algorithm as zlib's `crc32()` / the standard "CRC-32/ISO-HDLC" variant used by PNG/zip/gzip, so any standard CRC-32 library on the PC side matches: Python's `zlib.crc32`, .NET's `System.IO.Hashing.Crc32`, etc.). Reply `'K'`/`'E'`.
2. **Data** -- for each chunk (recommended chunk size 4096 bytes, matching `DB_UPLOAD_CHUNK_SIZE`; must not exceed it): `'D' <u16 len> <len bytes>`. Reply `'K'`/`'E'` per chunk. Chunks are written sequentially (offset tracked device-side) -- send them in order.
3. **Finalize** -- `'F'`. Device verifies total size and CRC, backs up the current DB, atomically swaps in the new one, and reboots via `esp_restart()`, which never returns to the code that would otherwise send a reply -- **no `'K'` is ever transmitted on success.** The CDC connection simply drops as the device restarts. Detect success by the port disconnecting/re-enumerating (or by a read timeout right after sending `'F'` with nothing else having gone wrong), not by waiting for an acknowledgement that isn't coming. A size/CRC mismatch, by contrast, does reply -- you get `'E'` and the device stays up with the old DB still active.

The file being uploaded must be a complete, valid GeroImperium SQLite database (all 5 tables: `Applications`, `KeyGroups`, `GeroImperiumKeys`, `KeyActions`, `GeneralSettings` -- the device validates their existence on boot and refuses to come up otherwise, error code E02).

## Incremental commands: `I` / `J` / `T`

Image payloads for `I` and `J` must be **exactly 32768 bytes** (`DB_IMAGE_BLOB_SIZE` = 128 x 128 x 2), already in DB storage format -- see "Image format" below. Anything else is rejected with `'E'` before the device even attempts the write.

### `I` -- set one Applications image
```
'I' <u8 app_index> <u32 len> <len bytes>
```
`app_index` is 0-based, ordered by the Applications table's `Id` column (row 0 = lowest `Id`, etc.) -- this is the same indexing the device's own Level-1 "Applications" view uses (display N shows `app_index` N). `len` must be 32768. Reply `'K'`/`'E'`. `'E'` if `app_index` doesn't correspond to an existing row.

### `J` -- set one key image
```
'J' <u16 key_group_id> <u8 position> <u32 len> <len bytes>
```
`key_group_id` is the actual `KeyGroups.Id` value (not an index -- resolve it first, e.g. from the DB you're editing, or from a prior `db_get_key_group`-style lookup if you're mirroring device state). `position` is 0-5 (button index within the group). `len` must be 32768. `'E'` if no `GeroImperiumKeys` row exists at that (key_group_id, position).

### `T` -- set or clear a key's shortcut
```
'T' <u16 key_group_id> <u8 position> <u8 modifiers> <u8 shortcut_len> <shortcut_len bytes ASCII>
```
`modifiers` bitmask: bit0=Ctrl, bit1=Shift, bit2=Alt, bit3=GUI/Win. `shortcut_len` 0 clears the key's mapping (no shortcut); otherwise it's the byte length of the ASCII token that follows (max 15 bytes -- see valid tokens below). `'E'` if no `GeroImperiumKeys` row exists at that (key_group_id, position).

The device reuses an existing `KeyActions` row when one already matches the given shortcut+modifiers combination rather than creating a duplicate -- no need to deduplicate on the PC side.

**Valid shortcut tokens** (case-insensitive; anything else is silently treated as "no shortcut" by the device's HID resolution, though it's still stored):
- Single letters `A`-`Z`, single digits `0`-`9`
- `F1`-`F12`
- `LEFT`, `RIGHT`, `UP`, `DOWN`
- `ENTER` / `RETURN`, `ESC` / `ESCAPE`, `BACKSPACE`, `TAB`, `SPACE`
- `HOME`, `END`, `PAGEUP`, `PAGEDOWN`
- `DELETE` / `DEL`

### Redraw behavior

If the edited slot happens to be what's currently on the device's screen, the device redraws it automatically (within roughly one input-task poll cycle, well under 100ms) -- no separate "refresh" command exists or is needed. If it's not currently visible, nothing visibly changes; the new content is simply what loads next time the user navigates there.

## Image format

Source images should be 128x128 (`DISPLAY_WIDTH` x `DISPLAY_HEIGHT`) before conversion -- the device does not resize. The stored/transmitted format is **not** plain RGB565: each 8-bit RGB channel is inverted (`255 - x`) before packing, packed as **B5G6R5** (blue in the high bits, not red), and written **little-endian**, 2 bytes per pixel, row-major, no padding -- exactly what `ImageProcessingService.cs::ConvertToRgb565Async` already produces for the existing bulk-import pipeline. Reuse that conversion path for the incremental commands too; the bytes you'd write into `Applications.ImageDataRgb565` for a bulk-import DB are the exact bytes to send as the `I`/`J` payload.

Do not send big-endian R5G6B5 (that's the device's internal framebuffer format, produced on-device by `decode_rgb565_blob()` -- never send that format over the wire).

## Database schema (what the PC app authors)

The device only reads/writes these columns; anything else in your authoring DB is your own business:

- **Applications**: `Id`, `ImageDataRgb565` (preferred image column), `ImageData` (fallback if the preferred column is NULL)
- **KeyGroups**: `Id`, `ApplicationId` (FK to Applications.Id)
- **GeroImperiumKeys**: `KeyGroupId` (FK), `Position` (0-5), `ImageDataRgb565` / `ImageData`, `KeyActionId` (FK to KeyActions.Id, nullable)
- **KeyActions**: `Id`, `ShortcutKey` (text token, see list above), `CtrlModifier`, `AltModifier`, `ShiftModifier`, `WinModifier` (each 0/1)
- **GeneralSettings**: required to exist (schema validation checks for the table) but not otherwise read by current firmware

Ordering matters for indexing: `app_index` and `group_index` are always "the Nth row `ORDER BY Id`", 0-based -- not an explicit sequence column. If your authoring app lets users reorder apps/groups, reordering means reassigning `Id` values (or simply relying on insertion order) rather than a separate sort-index field, since none exists.

## Practical workflow for the PC app

1. **First connect / full restore**: build a complete DB file locally (matching the schema above, images pre-converted per "Image format"), push it with the bulk `S`/`D`/`F` protocol, wait for the reboot.
2. **Live editing session**: keep your own local copy of the DB (or an in-memory model) as the source of truth for the UI. On each user edit (new icon, changed shortcut), convert/serialize just that one field and send the matching `I`/`J`/`T` command. Don't re-push the whole file for a single-field change.
3. **Error handling**: an `'E'` reply means the command was rejected outright (bad size, unknown slot) -- the device's live DB is unchanged. There's no partial-write state to clean up on the PC side.
4. **A dropped connection mid-bulk-upload has no recovery path -- avoid it.** There is no abort/cancel command, and `'S'` unconditionally rejects a new start (`'E'`) while the device still thinks a prior upload is in progress -- there's no timeout or disconnect detection that clears that state. If the PC app's connection drops after `'S'` but before `'F'` lands, the device is stuck (HID reporting stays suspended, `'S'` keeps failing) until it's power-cycled -- the old DB stays active and isn't corrupted, but the device needs a manual reboot to accept another upload. Make sure the connection is stable (and don't let the user cancel mid-transfer) before starting a bulk upload; the incremental commands have no such issue since each is a single self-contained round trip.

## Known limitation (read before relying on it)

Editing many keys/images in a tight loop works, but each incremental command is a full synchronous round trip (send, wait for `'K'`/`'E'`) -- there's no batching. For a large initial profile push, the bulk path is faster than hundreds of individual `I`/`J`/`T` calls despite the reboot cost.
