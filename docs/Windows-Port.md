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

Note that the "Nintendo advertisement that did not parse" warning keys off WinRT's `CompanyId`
property rather than any payload offset, so it fires even if the offsets below are wrong. A Joy-Con 2
that advertises while we scan cannot be silently missed.

### Advertisement offsets differ from CoreBluetooth — reasoned, not yet measured

This is the one place upstream's validated offsets cannot be copied verbatim. Upstream reads the
vendor ID at payload offset 5 and product ID at 7, because CoreBluetooth's manufacturer payload
*includes* the leading two-byte company identifier. WinRT splits those: `CompanyId` is a separate
property and `.Data` holds only the remainder, which should put the same fields at offsets 3 and 5.

`JoyCon2Advertisement` tries the Windows offsets first, falls back to the CoreBluetooth offsets, and
reports which matched via `PayloadIncludedCompanyId`. Both paths are unit-tested. The first hardware
run resolves it; record the answer here and then simplify the decoder.

## Open questions — Phase 1 transport spike

These are the questions `tools/Jc2Probe` exists to answer. None have been tested against hardware.

### 1. Does Windows require OS-level pairing before GATT works?

**Open. Highest-risk unknown in the project.** CoreBluetooth connects to an unpaired peripheral, so
upstream never had to solve this. On Windows, `BluetoothLEDevice.FromBluetoothAddressAsync` against
an unpaired device succeeds for some peripherals and fails for others, and Joy-Con 2 behavior is
unrecorded. If pairing is required, both the SYNC-button flow and the reconnect story change shape.

### 2. What notification cadence do we actually get?

**Open.** Windows 11 is documented to cap BLE throughput near 70 Hz; macOS measures 66–67 reports
per second. Expectation is that Windows lands in the same range, but this is an expectation.

Exit criterion: ≥60 decoded reports/second sustained for 5 minutes on a single Joy-Con.

### 3. Can two Joy-Con 2s each sustain ~66 Hz simultaneously on one radio?

**Open, and no prior evidence exists anywhere.** Upstream's Joy-Con 2 support is explicitly
single-controller, so this is new ground. Two concurrent BLE connections share radio time and
Windows exposes little scheduling control. If dual mode degrades both streams badly, that is a scope
decision to make before the device model and UI are built, not after.

Exit criterion: a measured number, pass or fail.

### 4. How does the Windows stack handle sleep/wake and range-loss reconnect?

**Open.** macOS's backoff timings should not be ported; re-derive them empirically. Note that the
Joy-Con 2 firmware has its own reconnect cooldown, which upstream documents and backs off to avoid.

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
