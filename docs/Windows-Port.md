# Windows port findings

Windows-specific behavior, recorded in the same evidence-first style as
[JoyCon2-Bluetooth.md](JoyCon2-Bluetooth.md): what was measured, what was tried and rejected, and
what is still unknown. Claims here should be traceable to an observation, not to an expectation.

Status legend: **verified** (observed on this machine), **assumed** (reasoned, not yet tested),
**open** (unknown, needs measurement).

## Environment

| Item | Value | Status |
| --- | --- | --- |
| Development OS | Windows 11, build 10.0.26200 (25H2) | verified |
| Minimum supported OS | Windows 11 22H2 (10.0.22621) | assumed — chosen from documented BLE throughput limits, not measured on 22H2 |
| .NET SDK | 9.0.316 | verified |
| Windows TFM | `net9.0-windows10.0.22621.0` | verified — builds and restores cleanly |

## Toolchain

**Verified 2026-07-25.** `dotnet build` and `dotnet test` both succeed on the Phase 0 scaffold, and
the split TFM arrangement works as intended: `JamCon.Core` and `JamCon.Core.Tests` build as plain
`net9.0` while the four platform projects build as `net9.0-windows10.0.22621.0`. The Windows SDK
projections restore from NuGet without any explicit package reference.

**Verified 2026-07-25 — WinRT GATT surface resolves.** A throwaway compile check confirmed that all
the types Phase 1 needs are projected and reachable from `JamCon.Bluetooth` under this TFM:

`BluetoothLEDevice`, `BluetoothLEAdvertisementWatcher`, `BluetoothLEManufacturerData`,
`GattDeviceService`, `GattCharacteristic`, `GattValueChangedEventArgs`, `GattCommunicationStatus`,
`GattClientCharacteristicConfigurationDescriptorValue`, `GattWriteOption`, `GattSession`.

`GattSession` resolving matters specifically: it carries `MaintainConnection` and the
`MaxPduSize` surface, which is the only real leverage Windows gives over connection behavior.
This removes "can .NET even reach the WinRT BLE API" from the Phase 1 risk list. It says nothing
about whether the API *works* against a Joy-Con 2 — that is still open.

## Radio availability is a distinct failure mode

**Verified 2026-07-25.** Starting a `BluetoothLEAdvertisementWatcher` while the radio is switched off
throws a bare `COMException (0x800710DF, ERROR_DEVICE_NOT_AVAILABLE)` from `watcher.Start()`. The
HRESULT is accurate but unactionable, and it looks like a code fault rather than a settings problem.

Worth recording because the obvious diagnostic misleads: on this machine Device Manager reported the
**MediaTek Bluetooth Adapter** as healthy (`Status: OK`) and `bthserv` as `Running` while the radio
was nonetheless off in Settings and every scan failed. Adapter health is not a usability signal.

`BluetoothRadioState.CheckAsync()` queries `Radio.GetRadiosAsync()` up front and distinguishes
*ready* / *radio off* / *no radio* / *unknown*. `JoyCon2Scanner.Start()` additionally translates that
HRESULT into `BluetoothUnavailableException`. The tray UI in Phase 5 should surface this state
directly rather than reporting "no controllers found", which is the wrong diagnosis and sends users
hunting for a controller problem that does not exist.

## Running the Phase 1 probe

```bash
dotnet run --project tools/Jc2Probe -- --seconds 300
```

```bash
dotnet run --project tools/Jc2Probe -- --dual --seconds 300
```

Flags: `--dual` (connect two controllers and measure each), `--scan-seconds N` (default 15),
`--seconds N` (default 300, matching the five-minute exit criterion), `--out DIR` (default
`./captures`).

Exit codes: `0` measured, `2` no Joy-Con 2 found, `3` nothing reached streaming, `4` Bluetooth
unusable.

Hold the SYNC button until the player LEDs chase before running. Each run writes a `.bin` (byte-exact
frames, the authoritative fixture) and a `.jsonl` sidecar (timestamps plus decoded fields). Raw
captures are gitignored; promote a chosen capture into `tests/**/Fixtures/` deliberately.

The probe prints the raw manufacturer-data bytes for every match, and reports which offset
interpretation succeeded — that is what settles the open advertisement-offset question below.

### Scanning is confirmed working, independent of any controller

**Verified 2026-07-25.** A 12-second scan observed **177 advertisements from 22 distinct devices**
while no Joy-Con 2 was in pairing mode. That rules out three things at once: the radio receives, the
`BluetoothLEAdvertisementWatcher` wiring is correct, and `ScanningMode.Active` is collecting normally.
A later "no Joy-Con 2 found" therefore points at the controller's advertising window, not the stack.

The same run reported **0 paired BLE devices**, confirming the Joy-Con 2 has never been paired to this
machine — so open question #1 is still genuinely open and has not been accidentally pre-answered by a
prior pairing.

The probe now prints a live counter during the scan (advertisements, distinct devices, Joy-Con 2
matches). Zero advertisements after a few seconds means a Windows Bluetooth problem; a healthy count
with zero matches means the controller is not advertising.

This section originally claimed the "Nintendo advertisement that did not parse" warning could not miss a Joy-Con 2. That was wrong: it compared the Bluetooth company ID with `0x057E`, the USB vendor ID, while the real company ID is `0x0553` (see below). Fixed in `89ed9da`.

### Advertisement format — verified

**Verified 2026-10-30** against the right Joy-Con 2 (`94:8E:6D:0A:16:AA`) in SYNC mode:

| Field | Observed |
| --- | --- |
| Advertisement type | `ConnectableUndirected`; the scan response carries nothing |
| Local name | none |
| Service UUIDs | none |
| Manufacturer section | company `0x0553`, payload `0100037E0566200001000000000000000F00000000000000` |

- The Bluetooth company ID is **`0x0553`**, Nintendo's SIG identifier — not `0x057E`, which is the
  USB vendor ID carried *inside* the payload. The first scanner revision compared against `0x057E`
  and so could never have flagged an unparsed Nintendo advertisement.
- Vendor ID `0x057E` sits at payload offset 3 and product ID `0x2066` at offset 5: the WinRT offsets,
  exactly as reasoned from upstream's CoreBluetooth offsets 5 and 7.
- The advertisement has **no name**. The `Joy-Con 2 (R)` shown in Windows Settings comes from GAP
  after connecting, so an unpaired Joy-Con 2 can only be identified by manufacturer data.

`JoyCon2Advertisement` still carries its CoreBluetooth-offset fallback. It is now known to be dead
and can be removed.

## Phase 1 results

Measured on 2026-10-30 with one right Joy-Con 2 and the machine's MediaTek adapter.

### 1. Does Windows require OS-level pairing before GATT works? — **No. Pairing breaks it.**

**Paired** (added through Settings → Add device): the link flapped between connected and
disconnected every few seconds, visibly in Settings before the probe even ran. Under the probe,
`GetGattServicesForUuidAsync` returned `Unreachable` on every one of 40 attempts across two minutes,
with `ConnectionStatusChanged` reporting `Connected` immediately before each failure.

**Unpaired** (device removed from Settings, address taken from the advertisement):
`FromBluetoothAddressAsync` → service discovery `Success` on the first attempt →
`Service found via UnpairedGatt` → every initialization command acknowledged → five minutes of
stable streaming with zero disconnects.

Why the paired path fails is **not verified**. The leading hypothesis is that the stored bond makes
Windows encrypt each reconnect and the controller firmware rejects it. CoreBluetooth never pairs, so
upstream never met this.

Consequences for the app:

- Never pair a Joy-Con 2 through Windows. Connect by address from its advertisement.
- A user who has already added one in Settings must remove it. The Phase 5 UI should detect a paired
  Joy-Con 2 and say so, rather than silently failing.
- Pressing **SYNC** during a live session drops the link — observed directly. The UI should only ask
  for SYNC while discovering.
- Pressing **HOME** wakes a sleeping Switch 2, and the controller reconnects to the console instead.
  Sleep is not off; the console must be fully powered off or in airplane mode.

### 2. What notification cadence do we actually get? — **Same as macOS. Exit criterion met.**

| Measure | Result |
| --- | --- |
| Frames | 20,088 over 300.9 s |
| Average rate | **66.8 reports/s** (criterion ≥ 60) |
| Median rate | 66.7 Hz |
| Per-second range | 64–70; 251 of 298 one-second windows at 66–68 |
| Worst gap | 102.1 ms (roughly six missed connection events, once) |
| Undecodable frames | 0 |
| Zero-filled IMU frames | 1, at startup, as the spec predicts |
| Disconnects | 0 |
| ATT `MaxPduSize` | 23 before connect, **512** after |
| Report-rate request | `0x0085` (133 Hz) accepted |

`RequestPreferredConnectionParameters(ThroughputOptimized)` returned `Success`, but no run was made
without it, so whether it is *why* the rate matches macOS is untested.

The 102 ms worst gap matters for Phase 2: gyro integration and smoothing must tolerate an occasional
multi-interval gap rather than assume a fixed 15 ms step.

### 3. Can two Joy-Con 2s each sustain ~66 Hz simultaneously on one radio? — **Still open.**

Next run: `--dual`, both controllers removed from Settings, SYNC pressed on each during the scan.

### 4. How does the Windows stack handle sleep/wake and range-loss reconnect? — **Partly answered.**

SYNC and HOME both end a session, as above. Measured so far with the tray app (2026-10-30):

- **Idle:** a connected Joy-Con 2 streamed for 26 minutes with no drop, until the app was
  deliberately closed. Whether it was handled during that time was not recorded, so this shows no
  spontaneous drop over 26 minutes rather than proving how a fully idle controller behaves.
- **App relaunch:** reconnected to the remembered controller in 12 s with no SYNC and no button
  press. Windows had kept the Bluetooth link up after the old process exited (the device reported
  `Connected` with a 512-byte MTU already negotiated), and a connected device does not advertise,
  so the advertisement wait found nothing and the rationed direct attempt is what connected.
  Checking for an already-connected device first would cut this to about 2 s; not yet done.
- **First connection after SYNC:** took four attempts over 40 s. In each failure the link came up,
  service discovery returned `Unreachable`, and the link dropped within a second. Unverified
  explanation: SYNC was being pressed during those attempts, which is known to drop a live link.

- **Long session:** a later session streamed for 89 minutes with no drop, until closed for an update.
- **Relaunch after 35 s:** this time the device was already `Disconnected` and never answered the
  direct attempts. Windows had let the link go in the longer gap, and the controller, with no link,
  went to sleep. So relaunch reconnects on its own only within roughly the 10–20 s Windows keeps
  an orphaned link; after that the controller needs waking.

Still untested: range loss, Bluetooth off/on, PC sleep/wake, and — now the important one —
whether a normal button press wakes a sleeping controller, or only SYNC does. Upstream says the
controller "may not advertise again by itself" after the host sleeps.

**Idle sleep.** After 10 minutes without use (`ActivityDetector`: a button, the stick pushed, or
motion over 20 °/s), the link disconnects deliberately so the controller can power down, and enters
an `Asleep` state. Disconnecting is the only sleep mechanism known: upstream has no power-off
command. While asleep the link accepts any advertisement at all from the remembered address as a
wake signal, including directed advertisements that carry no manufacturer data, but ignores
everything for the first 10 s so a controller briefly advertising on its way to sleep is not
reconnected. A controller that wakes and then goes unused for 60 s sleeps again. Disposing the
session now also releases the GATT service and clears `MaintainConnection`, since an undisposed
handle could keep Windows' link, and therefore the controller, awake.

**Test harness:** the tray app (`src/JamCon.App`), which runs `JoyCon2Link` continuously and logs
every transition to `%LOCALAPPDATA%\JamCon.Win\logs\jamcon-YYYYMMDD.log`: drop reason, how the
controller was found again (advertisement or direct attempt), and how long reconnecting took. It
remembers the controller's address in `%LOCALAPPDATA%\JamCon.Win\settings.json`.

Starting policy, to be replaced by measured values: wait 3 s after a drop, then wait for an
advertisement in 10 s windows, with a direct connection attempt at most every 30 s. A stream with
no reports for 2 s counts as dropped. Upstream's warning that repeated attempts push the firmware
into a reconnect cooldown is why direct attempts are rationed.

Scenarios to run, each followed by reading the log:

| Scenario | What it answers |
| --- | --- |
| Leave the controller untouched | Does an idle Joy-Con 2 drop or sleep, and after how long? |
| Walk out of range and back | Does it come back on its own, or need a button press? |
| Press a normal button after a drop | Can it reconnect without SYNC? |
| Turn Windows Bluetooth off, then on | Radio-off detection and recovery |
| Sleep the PC, wake it | Resume handling; `RequestReconnect` fires on resume |
| Exit and relaunch the app | Reconnecting to the remembered controller without SYNC |

### Initialization sequence — verified

All commands acknowledged in the documented order: `setFeatureMask`, `enableFeatures`,
`readStickCalibration`, `setPlayerOneLED`, then the report-rate descriptor and the input
subscription. Factory calibration: center (2077, 2093), positive range (1215, 1179), negative range
(1213, 1194). The resting stick read 2032–2066 on X and 2025–2079 on Y across separate connections,
within the 150-count correction radius of that anchor.

### Button bits — verified

Each button pressed alone and held about two seconds; the bit is the one that changed. Bit 0 is the
low bit of the four-byte field at offset `0x04`.

| Button | Bit | Byte `0x04` value / byte `0x05` value |
| --- | ---: | --- |
| Y | 0 | `0x01` |
| X | 1 | `0x02` |
| B | 2 | `0x04` |
| A | 3 | `0x08` |
| SR | 4 | `0x10` |
| SL | 5 | `0x20` |
| R | 6 | `0x40` |
| ZR | 7 | `0x80` |
| Plus | 9 | byte `0x05`: `0x02` |
| Stick click | 10 | byte `0x05`: `0x04` |
| C | 14 | byte `0x05`: `0x40` |

Every button lands where upstream's Joy-Con family layout already expects it. C's position is new
information: upstream leaves C unmapped and never recorded it. HOME was deliberately not pressed.

Bits 29–31 (byte `0x07` = `0xE0`) are set at rest and **never changed** during any press. Byte `0x07`
is most likely status rather than buttons. Upstream's `controlBytes` copies only bytes `0x04`–`0x06`,
and the C# mapping layer must do the same, or it will read three phantom held buttons.

### Golden-vector fixture

`tests/JamCon.Core.Tests/Fixtures/jc2-right-5min.bin` (byte-exact, format v1) with its `.jsonl`
sidecar: the 20,088-frame run above. Known content, by sidecar timestamp, for assertions:

| t (s) | Held |
| ---: | --- |
| 38.2 | A |
| 41.1 | B |
| 46.1 | X |
| 50.5 | Y |
| 54.8 | R |
| 58.9 | ZR |
| 63.3 | Plus |
| 68.7 | Stick click |
| 84.7 | C |
| 193.9 | SL |
| 198.1 | SR |
| 203.2 | C |

Motion alternates between waving the controller and holding it still throughout.

## Phase 2 and 3: core port and SendInput output

The core (gyro processing, mapping, press/hold engine, output coordination) is ported into
`JamCon.Core` with upstream's tests translated, and `JamCon.Output` drives the cursor and keyboard
through SendInput. 129 Core tests and 23 Output tests pass.

**Verification against hardware.** A frame-for-frame comparison with the Swift decoder was not
possible (no Swift toolchain on Windows). Instead the golden-vector tests replay all 20,088 frames of
the five-minute capture through the whole engine with the default profile and check the result
against what physically happened: exactly the 16 expected outputs, in order, for the timed button
presses, and cursor motion only on frames where the X clutch was held. Two interactive tests
(`JAMCON_INTERACTIVE_TESTS=1`) drive the real cursor and confirm that absolute moves land on exact
pixels on every corner of both monitors of a two-monitor layout with a gap between them, and that
quarter-pixel steps accumulate rather than vanish.

**Still unverified on hardware:** how the gyro mouse *feels*. The tuning is upstream's, validated
on macOS for this controller and grip; whether it suits Windows desktop use is for a person to judge.

### Traps found during the port

Each would have produced subtly wrong behaviour rather than an error:

- **Arrow keys injected by scan code arrive as numpad keys.** `MapVirtualKey(…, MAPVK_VK_TO_VSC_EX)`
  returns the Windows key as `0xE05B` but the arrows as bare `0x48`/`0x50`, so the extended flag
  cannot be derived from it alone. Without the flag, Down-arrow types "2" when NumLock is on. Caught
  by a unit test; fixed by also flagging known extended keys by virtual-key code.
- **Absolute coordinates.** The common `pixel × 65535 / width` lands a pixel short over much of the
  screen, which would show as slow creep up and left. The conversion rounds up instead, which
  recovers the exact pixel whether Windows truncates or rounds.
- **Rounding mode.** Swift's `rounded()` takes halves away from zero; C#'s `Math.Round` defaults to
  banker's rounding. Matters in the settings normalizer.
- **Integer negation.** The left controller's remap negates raw gyro values; negating
  `short.MinValue` wraps silently in C# (Swift would trap). Values widen to `int` first.
- **xUnit's `Assert.Equal(a, b, precision)` rounds both sides** rather than taking a tolerance, so
  upstream's `accuracy:` assertions are translated to the tolerance overload.
- **H.NotifyIcon's `ForceCreate` enables Windows Efficiency Mode by default**, which throttles the
  process. Declined explicitly. Version 2.4.x also ships no .NET 9 build, so 2.3.2 is pinned.
- **Windows 11 can ignore `timeBeginPeriod` for a process with no visible window**, which describes
  a tray app. 1 ms sleeps then quietly become 15.6 ms, which would cap motion at ~64 Hz. The pacing
  loop uses a high-resolution waitable timer instead, and the process opts out of power throttling
  (EcoQoS and the timer-resolution rule) via `SetProcessInformation`.
- **The .NET 10 SDK on CI compiles as C# 14**, where `array.Reverse()` binds to the in-place span
  overload and returns void. `global.json` pins the 9.0 SDK so CI and local builds agree.

### Smooth motion

The controller reports at ~67 Hz, so emitting each report's motion at once moves the cursor in
15 ms steps: visibly coarser than a mouse on a 120 Hz display. `PacedOutputSink` spreads each
report's cursor and scroll delta evenly over the expected interval to the next report and emits at
240 Hz. Total motion is conserved exactly (tested); the cost is about 7.5 ms of average latency.
Any click or key first flushes pending motion, so it lands where the cursor was heading.

### Radial menu

Ported from upstream: hold B, point, release. The overlay is a transparent, click-through,
never-activating topmost window, so the selected key reaches the app that had focus. Upstream hid
the system cursor while the menu was open; Windows has no supported way to hide another app's
cursor, so the cursor holds still instead. Drawing and selection share one angle function, and a
test checks the middle of every drawn slice selects that slice. A preview (`JamCon.App.exe
--preview-radial-menu`) renders it without a controller; verified on this machine's scaled display.

## Decisions carried in from planning

Recorded so they are not silently revisited. Rationale is in
[JamCon-Win-Plan.md](JamCon-Win-Plan.md).

| Area | Decision | Basis |
| --- | --- | --- |
| BLE layer | WinRT `Windows.Devices.Bluetooth` directly, no third-party wrapper | The proven layer is the OS; wrappers add distance from connection-interval behavior |
| Mouse output | `MOUSEEVENTF_ABSOLUTE` against virtual-desktop coordinates | Pointer ballistics ("Enhance pointer precision" + speed slider) distort `MOUSEEVENTF_MOVE` and make gyro tuning unfalsifiable |
| Keyboard output | `KEYEVENTF_SCANCODE` in preference to virtual keys | Many games read scancodes directly and ignore VK injection |
| Elevation | `asInvoker`; no silent admin request | UIPI blocks injection into elevated windows. Documented as a limitation instead |
| DPI | `PerMonitorV2` in the app manifest | Absolute coordinates get silently rescaled for a DPI-unaware process on mixed-DPI setups |
| Gamepad output | ViGEm behind an `IOutputSink` interface | ViGEmBus was archived Nov 2023 with no released successor; the interface is the mitigation |

## Traps to respect during the port

Carried from planning; each is a known Windows behavior rather than a hypothetical.

- **Sub-pixel accumulation.** `SendInput` takes integers. Carry the fractional remainder between
  calls or slow, precise gyro motion vanishes entirely.
- **Scroll accumulation.** `MOUSEEVENTF_WHEEL` / `HWHEEL` with `WHEEL_DELTA = 120`, same concern.
- **Timer granularity.** `CACurrentMediaTime()` maps to `Stopwatch.GetTimestamp()`. Call
  `timeBeginPeriod(1)` and raise input-thread priority; .NET's default timer granularity adds
  jitter that gyro smoothing will faithfully render as visible cursor wobble.
- **Decoder drift.** The Phase 1 raw frame dump becomes a committed golden-vector fixture. Without
  it, a C# decoder that diverges subtly from the validated Swift one produces wrong input that is
  very hard to trace.
