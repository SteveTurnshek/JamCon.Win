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
