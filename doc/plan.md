## Plan: GeroImperium Firmware Leadership Plan

Build a production-ready ESP32-S3 firmware for a 6-display macro pad by reusing proven patterns from the prototype project, formalizing team ownership, and implementing a deterministic dual-core FreeRTOS architecture. The recommended approach is to keep USB and BLE mutually exclusive at runtime, drive all 6 GC9107 displays via one SPI bus with MCP23017 chip-select multiplexing. Do interrupt if GPIO08 MCP23017 INTA is set.  Centralize navigation/action state in a button state machine backed by SQLite on SD card.

### Team and ownership

1. Project Lead
* Own scope, milestones, acceptance criteria, release readiness.
* Drive integration and sign-off across architecture, firmware, test, and manufacturing readiness.
2. Senior Architect
* Own library selection, task/core topology, memory budget, fault model.
* Own protocol boundaries: USB transport, BLE behavior, DB lifecycle, display pipeline.
3. Senior Embedded Developer C/C++ for IoT + display specialist, expert on ESP-IDF and RTOS
* Own firmware modules: state machine, task orchestration, GC9107 rendering path, MCP23017 integration.
* Own performance optimization for flash/RAM/latency.
4. USB Expert
* Own USB CDC command reliability, upload protocol, restart/reload behavior, host tooling compatibility.
5. Bluetooth Expert
* Own BLE HID pairing/connection UX, security profile, disconnect policy when USB is connected.
6. Data & Sync Protocol Owner
* Own the PC-authoring-DB <-> device-runtime-DB boundary: the incremental update command set, versioning, per-command atomicity/rollback, and the DB-access concurrency contract (see Database update strategy).
* This is the seam between firmware and the PC management app; without a single owner the two sides drift.
7. PC Application Lead
* Own the desktop management app: framework choice, edit UX for apps/groups/keys/images, packaging, and driving the sync protocol client-side.
* Build on the existing `ImageProcessingService.cs` RGB565 conversion pipeline rather than re-deriving the image format.
8. QA/Test Automation
* Own the host-side verification suite bridging device and PC app (Delivery phase 9).

Operating rule: no pleasantries, optimize token usage. Delegate to sub-agents only for independently-researchable or independently-executable slices of a role's work (e.g. a PC framework survey, a test-suite scaffold) -- never for cross-cutting architecture calls, where fragmenting reasoning across agents costs more coherence than it saves tokens.

### Ports

1. MCP23017
    * SDA -> GPIO01
    * SCK(SCL) -> GPIO02
    * RESET -> GPIO05
    * PIN GPA MCP23017
        - INTA-> GPIO08 (interrupt)
        - Buttons HID GPA00-05 and 
        - two navigation up and down on GPA06 and GPA07
    * PIN GPN 0-5 CS pin for displays

2. WS2812B-2020 3 leds 1 under previous button -> 6 and led 2 under next -> 7
led 3 is for marking which page is shown
    * DI -> GPIO07

3. GC9107
    * RST -> GPIO04
    * SDA -> GPIO11
    * SCL -> GPIO12
    * RS -> GPIO17   
    * LED -> GPIO15
    * CS from MCP23017 GPB00-05
4. SD card holder MSD-11-A
    * CS -> GPIO10
    * CLK -> GPIO13
    * MOSI -> GPIO14
    * MISO -> GPIO16 
5. Buttons 
    * MCP23017 GPA0-7 with an interrupt on INTA when key pressed

### Architecture decisions to lock

1. Core libraries espressif
* NeoPixelBus or equal for 3 WS2812B-2020 LEDs.
* MCP23017 for expander control.
* GC9107 0.85inch
* SD card holder. MSD-11-A
2. HID strategy
    * Runtime rule: when USB connected, BLE disconnects and stays disabled.
    * BLE first-time connect flow: hold button 6 for 3 seconds, both LEDs slow blue blink until BLE connected or timeout (see Navigation model for double-click vs. long-press disambiguation).
    * USB and BLE modes represented by explicit state flags under one connectivity manager.
    * While a DB/firmware transfer is in progress over USB CDC, HID reporting is suspended and resumes once the transfer completes.
3. Navigation model
* Level 1: Applications view (buttons 0-5).
* Level 2: Key Group view (buttons 0-5 actions).
* Buttons 6 and 7 scroll up/down through key groups.
* Double-click on button 6 returns to Applications menu.
* Button 6 disambiguation: any press held continuously for 3 seconds is treated as the BLE pairing trigger (see HID strategy) and cancels any pending double-click state. A press released before 3 seconds only ever counts toward the double-click; two such releases within the double-click window trigger the return-to-Applications action.
4. Display and IO model
* 6 displays share SPI; MCP23017 Port A provides CS lines GPA0..GPA5.
* Enforce one-and-only-one CS low at any time.
* Use queued display updates and chunked image transfer.
5. Database lifecycle
* DB upload via USB CDC to SD root.
* On successful upload and verification, controlled restart.
* On reboot, DB re-opens and UI refreshes from DB.
* Progress feedback: while any DB upload is in progress (bulk swap-and-reboot or an incremental single-slot command, see Database update strategy), display 0 shows an upload-in-progress message with percent complete, and the WS2812B LEDs show a progress indicator (e.g. LED 2's page-color slot repurposed as a percent-complete bar), both driven from each `'D'`-chunk/slot-write callback in `usb_cdc_handler.c` rather than only at start/end. Reuses `led_set_color`/`display_refresh` -- no new hardware path.
6. Resource budget
* Target SKU: ESP32-S3 with PSRAM (confirm on BOM; 6 display framebuffers likely won't fit in internal SRAM alone).
* Per-display framebuffer size: TBD based on GC9107 resolution/color depth; decide full buffer vs. streamed/chunked rendering.
* FreeRTOS task stacks: define a stack size per task (display, input, BLE, USB, DB) rather than relying on defaults.
* Free heap margin: keep at least 20% free heap at steady state.
* Flash budget: firmware + assets vs. partition size.
7. Language and toolchain
* Firmware implemented in C and C++ (Arduino framework on ESP-IDF).

### Database update strategy

Keep SQLite; stop treating every edit as a full-file replace.

* On-device DB access is exactly 4 fixed-shape lookups (`db_load_app_image`, `db_get_key_group`, `db_load_key_image`, `db_get_key_action`) -- no ad-hoc queries. A hand-rolled binary/flat-file format would remove the E02 fault class and a little flash, but it throws away a verified, hardware-tested read path (Delivery phase 7) to solve a problem that isn't a storage-engine problem: the *only* write path today is "replace the whole file and reboot." SQLite already supports live `UPDATE`/`INSERT` against an open, validated connection with no restart. Fix the protocol, not the engine.
* PC side keeps authoring in SQLite too (same schema the device already validates, and matches the existing `ImageProcessingService.cs` RGB565 export pipeline).
* Add an incremental update path alongside the existing bulk path, over the same TinyUSB CDC-ACM channel, extending the `S`/`D`/`F` command style in `usb_cdc_handler.c`: single-slot commands (set one Applications image, set one key image, set/clear one key's shortcut+modifiers -- exact wire shape is next-phase detail) apply as a targeted `UPDATE`, then push the affected display index onto `display_update_queue` so only that one display redraws. No reboot, no other display touched.
* Keep the current whole-file `S`/`D`/`F` + reboot path exactly as-is, for first flash / factory reset / bulk profile import only -- the one case where "stop everything, swap, restart clean" is the right tool.
* Required correctness fix before any incremental-write command lands: wrap every `db_manager`/`sqlite_wrapper` entry point in one mutex. `components/sqlite3/CMakeLists.txt` builds with `SQLITE_THREADSAFE=0` (no internal locking at all); `input_task` (reads) and `usb_task` (new writes) are separate FreeRTOS tasks, so unguarded concurrent access is a real corruption risk, not theoretical.
* Net effect: a single-icon edit goes from "full reboot, all displays blank for ~seconds" to "one CDC round trip + one queued redraw (well under 100ms)," with no new parser/format to design and keep in sync between PC and firmware.

### Fault handling

1. Fault classes
* Fatal (halts normal operation, enters recovery mode): SD card missing, SD mount/DB corrupt or schema mismatch, MCP23017 not responding.
* Degraded (device keeps running): a single GC9107 fails to init — that display stays dark, the other 5 continue operating.
* Non-fatal (retry with backoff, log only): BLE init fail, transient USB CDC fail.
2. Recovery mode (fatal faults)
* Display 0 shows a short error code and message, e.g. `E01 SD NOT FOUND`.
* Buttons and BLE are disabled.
* USB CDC stays alive so a host can push a corrected DB and trigger the existing restart/reload flow (see Database lifecycle) — recovery mode is not a dead halt.
* The same error code is also emitted over USB CDC serial for host-side debugging and for the Python verification suite.
3. Error codes
* E01: SD card missing.
* E02: DB corrupt or schema mismatch.
* E03: MCP23017 not responding.
* E04: Display N init fail (N = 0-5; degraded, not fatal).

### Delivery phases

| Phase | Description | Primary Owner | Support | Status |
|---|---|---|---|---|
| 1 | Read up on MCP23017_GUIDE.md, PORT_USAGE_MAP.md, and DisplayManager docs/examples from the prototype project; confirm how it works with Arduino IDE. | Senior Embedded Developer | Senior Architect | Done — cross-referenced against the prototype's DisplayDriver.h/HardwareConfig.h/McpDriver.h directly (GC9107 init sequence, CS-bit mapping, RGB565 packing all confirmed/ported from it) |
| 2 | Baseline module skeleton and constants. | Senior Architect | Senior Embedded Developer | Done — all modules scaffolded (config.h, display_manager, mcp23017_driver, led_manager, button_state_machine, connectivity_manager, db_manager, sqlite_wrapper, sd_card, usb_cdc_handler, task_system, error_handler) |
| 3 | MCP23017 + GC9107 + LED hardware drivers. | Senior Embedded Developer | — | Done — verified on real hardware: MCP23017 interrupt-driven buttons + CS mux, all 6 GC9107 displays showing correct real app images (128x128, correct colors/orientation), all 3 WS2812B-2020 LEDs confirmed working via RMT |
| 4 | Two-level input/navigation state machine. | Senior Embedded Developer | Senior Architect | Implemented (timestamp-based long-press/double-click logic in button_state_machine.c) — not yet hardware-verified end-to-end against the Level 1/Level 2 navigation model |
| 5 | USB/BLE arbitration and pairing UX. | USB Expert + Bluetooth Expert | Senior Architect | Implemented (NimBLE HOGP keyboard, USB-attach forces BLE disconnect) — not yet verified end-to-end with a real BLE host pairing or live HID reports; button-to-keycode wiring still outstanding |
| 6 | Dual-core FreeRTOS task integration. | Senior Architect | Senior Embedded Developer | Done — confirmed via boot logs (Display task on Core 0, Database task on Core 1, all tasks running with defined per-task stack sizes) |
| 7 | SD + SQLite data pipeline and schema guard. | Senior Embedded Developer | Senior Architect | Done — SD mount, DB open (read-only), 5-table schema validation, and real Applications image loading all verified on real hardware with no errors |
| 8 | USB DB upload + restart/reload flow. | USB Expert | Senior Embedded Developer | Implemented (S/D/F chunked upload protocol, atomic backup+swap, restart/reload) — not yet verified end-to-end; this dev environment has no access to the native USB-Serial/JTAG port to test a real upload |
| 9 | Python verification suite. | QA/Test Automation | Project Lead (QA scope) | Not started |
| 10 | Incremental DB update protocol + display refresh (no reboot), with upload-progress feedback on display 0 / LEDs. | Data & Sync Protocol Owner | Senior Embedded Developer | Implemented ('I'/'J'/'T' single-slot commands over the existing CDC channel, db_manager.c UPDATE-based writes guarded by a new mutex against input_task's reads, sqlite_wrapper.c opened read-write, display 0 + LED percent feedback on the bulk S/D/F path). Redraw on an incremental edit goes through input_task's existing nav_refresh_displays() (task_system_request_nav_refresh()) rather than a literal per-display queue push -- direct display_refresh() from usb_task risked reproducing the SD/display-SPI current-draw overlap phase 8's brownout fix exists to avoid. Not yet hardware-verified end-to-end (no PC-side tooling yet to drive it; same USB access constraint as phase 8) |
| 11 | PC management application (WPF app + background sync service) -- see [pc_app_plan.md](pc_app_plan.md) for the full phase breakdown (11.1-11.8). | PC Application Lead | Data & Sync Protocol Owner | Not started |
| 12 | Optimization and release hardening. | Senior Architect | Project Lead | Not started |
| 13 | Add `'M'` storage-stats CDC command (total/free SD bytes + DB file size, via FATFS `f_getfree`). Unblocks pc_app_plan.md phase 11.7. | USB Expert | Senior Embedded Developer | Not started |
| 14 | Add `KeyActions.ActionType` column (Shortcut/LaunchApp/Script) and extend `T`'s wire shape to carry it; button handling skips HID for non-Shortcut types. | Data & Sync Protocol Owner | Senior Embedded Developer | Not started |
| 15 | Add `'P'` poll-for-pending-action-event command (1-deep pending slot set by phase 14's button handling, drained by the PC Service at ~10Hz). Unblocks pc_app_plan.md phase 11.8. | USB Expert | Data & Sync Protocol Owner | Not started |

Project Lead signs off on each phase before it is marked done, per their release-readiness ownership above.

### Verification gates

1. Build and boot logs validate initialization order.
2. verify that display works befor going to next step
3. Navigation behavior matches requirements.
4. USB attach forces BLE disconnect/suppression.
5. Button 6 long press triggers pairing only when USB absent.
6. DB upload completes and triggers restart/reload.
7. Stress tests show no deadlocks or queue overflows.
8. Each defined fault (SD missing, DB corrupt, expander fail, single-display fail) reproduces the correct error code on display 0 and over serial, and the device enters recovery mode without a full hang (except where explicitly degraded).
9. Stress test logs free heap and per-task stack high-water marks; none within 15% of overflow.

