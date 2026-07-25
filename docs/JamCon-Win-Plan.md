# JamCon.Win — Joy-Con 2 on Windows

Fork of `jturnshek/JamCon`, gutted to a Windows-only C#/.NET application supporting Switch 2
Joy-Cons. macOS support is dropped, not maintained.

Date: 2026-07-25 (rev 2)

---

## 1. What we're actually porting

The headline finding from reading upstream: **the hard part is already done.**

`docs/JoyCon2-Bluetooth.md` is a complete, hardware-validated Joy-Con 2 BLE spec — GATT service
and characteristic UUIDs, the exact initialization sequence, the `0x05` common-report byte layout,
the gyro scaling constant, and a written record of which alternate paths were tried and rejected
and why. There is a working macOS implementation behind it (`JoyCon2BLESession.swift` 851 LOC,
`JoyCon2BLEProtocol.swift` 319, `JoyCon2BLEInputDeviceBackend.swift` 338).

So this is not a reverse-engineering project. It's a **platform-layer** project:

| macOS | Windows replacement | Notes |
| --- | --- | --- |
| CoreBluetooth | `Windows.Devices.Bluetooth.GenericAttributeProfile` (WinRT) | Highest-risk swap. Different pairing model, far less cadence control. |
| CGEvent (`MouseController`) | `SendInput` via P/Invoke | Straightforward, with pointer-ballistics caveats. |
| SwiftUI / AppKit menu bar (29 view files) | WPF tray app | Full rewrite, no reuse. |

Of 23,151 Swift LOC across 87 files, the genuinely reusable *logic* — protocol decode, gyro
processing, button mapping, engine policy — is roughly **2,400 LOC**. That's the port surface.
Everything else is either UI or macOS-only device backends (original Joy-Con via IOKit HID, PS VR2
Sense via GameController, Logitech G502X via HID++) that we delete.

## 2. Decisions taken

| Decision | Choice |
| --- | --- |
| Strategy | Separate C#/.NET app. Docs are the contract, not code. |
| macOS | **Dropped.** Swift sources deleted from the fork. |
| Minimum OS | **Windows 11 22H2+.** Win10's ~20 Hz BLE ceiling makes gyro mouse unusable. |
| Output modes | Gyro mouse + keyboard first; ViGEm virtual gamepad second. |
| Device modes | **Both** single Joy-Con (left or right, one-handed) and dual-as-one-device. |
| Naming | `JamCon.Win`, repo `sturnshek/JamCon.Win`. Not merging upstream. |
| BLE layer | WinRT directly — see below. |

### The "proven BLE layer" is WinRT itself

You asked to vendor a proven BLE layer where one fits. The honest answer is that the right
dependency here is the OS. `Windows.Devices.Bluetooth` is first-party, is what every Windows
Joy-Con tool ultimately calls, and is reachable from .NET through CsWinRT with zero third-party
packages. Cross-platform wrappers (Plugin.BLE, SimpleBLE) would only insert a layer between us and
the connection-interval behavior we care most about.

Prior art we *read* rather than take:

- [`ndeadly/switch2_controller_research`](https://github.com/ndeadly/switch2_controller_research) — protocol confirmation.
- [SDL's `SDL_hidapi_switch2.c`](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_switch2.c) — independent decoder to cross-check against.
- [`TommyWabg/Switch2Connect`](https://github.com/TommyWabg/Switch2Connect) — closest existing Windows tool. Read specifically for pairing and connection-interval handling.
- [`matiaspalmac/everything-imu`](https://github.com/matiaspalmac/everything-imu) — Rust JC2 driver; agrees with upstream's gyro coefficient.

Check licenses before copying anything. Upstream JamCon is MIT; stay MIT-compatible.

## 3. A constraint that shapes everything

**I cannot build or test C# in this session.** The sandbox I run in is Linux, has no .NET SDK, no
root to install one, and NuGet is blocked by the network proxy. I also obviously have no Joy-Con 2
and no Bluetooth radio.

Consequence: every compile, every test run, every hardware check happens on your machine, and each
round trip costs you a message. The plan below is built to minimize round trips rather than to
minimize my effort. Concretely:

- **`JamCon.Core` targets plain `net9.0`, not `net9.0-windows`.** No WinRT, no P/Invoke, no UI —
  pure protocol decode, gyro math, mapping, and engine policy. This is deliberate: it means the
  bulk of the code is testable with `dotnet test` and independently reviewable, and it keeps the
  Windows-specific surface small enough to reason about without a compiler.
- **Windows TFM confined to three projects:** `JamCon.Bluetooth` (WinRT GATT), `JamCon.Output`
  (SendInput / ViGEm), `JamCon.App` (WPF tray).
- **Deliveries are large and self-contained**, with tests, rather than incremental drips.
- **Failures come back as pasted compiler/test output**, which I fix in batch.

Worth saying plainly: for the tight edit-build-fix loop, running Claude Code locally on your Windows
box will be far cheaper per iteration than round-tripping through this session, because it can
actually run `dotnet build` itself. The high-value use of *this* session is design, protocol work,
and writing the substantial chunks — not chasing semicolons.

## 4. Phases

### Phase 0 — Fork and gut

1. Fork `jturnshek/JamCon` → `sturnshek/JamCon.Win`. Clone from a Windows terminal (git can't take
   lockfiles on the sandbox mount).
2. **Tag the pre-deletion commit `macos-reference`** before removing anything, so the validated
   Swift implementation stays reachable in history.
3. Copy seven Swift files into `reference/swift/` as read-only port sources, then delete the rest of
   the Swift tree. Having them sitting next to the C# during Phase 2 avoids constant git archaeology;
   delete the directory once the port is validated.
   - `JoyCon2BLEProtocol.swift`, `JoyCon2BLESession.swift`, `JoyCon2BLEInputDeviceBackend.swift`,
     `GyroProcessor.swift`, `JoyConButtonMapping.swift`, `InputEngine+JoyCon.swift`,
     `MouseController.swift`
4. Delete: `Sources/`, `Tests/`, `JamCon.xcodeproj/`, `.swiftpm/`, `project.yml`, `dev.sh`,
   `relaunch.sh`, `release.sh`, `release.env.example`, `homebrew/`, `Resources/`, and the macOS docs
   (`ARCHITECTURE.md`, `DEVELOPMENT.md`, `USAGE.md`, `TROUBLESHOOTING.md`, `JoyCon-HID-Protocol.md`,
   `Sense-HID-Protocol.md`, `RELEASING.md`).
5. **Keep:** `LICENSE` (MIT — attribution to jturnshek is a legal obligation, not a courtesy),
   `docs/JoyCon2-Bluetooth.md` (the crown jewel), `CODE_OF_CONDUCT.md`, `SECURITY.md`.
6. Rewrite `README.md`: Windows 11 22H2+, Joy-Con 2 only, clear credit to upstream.
7. Scaffold the solution:

```
JamCon.Win.sln
  src/JamCon.Core/          net9.0          protocol, gyro, mapping, engine   ← testable anywhere
  src/JamCon.Bluetooth/     net9.0-windows10.0.22621.0   WinRT GATT
  src/JamCon.Output/        net9.0-windows10.0.22621.0   SendInput, ViGEm
  src/JamCon.App/           net9.0-windows10.0.22621.0   WPF tray UI
  tools/Jc2Probe/           net9.0-windows10.0.22621.0   Phase 1 console spike
  tests/JamCon.Core.Tests/  net9.0          xUnit
  reference/swift/                          temporary port source
  docs/JoyCon2-Bluetooth.md                 inherited spec
  docs/Windows-Port.md                      new: Windows findings, same evidence-first style
```

8. CI: `windows-latest`, `dotnet build` + `dotnet test`. Delete the macOS workflow.

### Phase 1 — BLE transport spike ← *gate*

`tools/Jc2Probe`. Console only. No mapping, no mouse output, no UI. Connect, initialize, print
decoded reports and a measured rate.

- Scan advertisements, match Nintendo manufacturer data (vendor `0x057E`), filter product `0x2066`
  (right) / `0x2067` (left).
- Run upstream's initialization sequence verbatim: subscribe command responses → `featureMask =
  0x07` → read 9-byte stick calibration at `0x000130A8` → select player LED 1 → write report-rate
  descriptor `0x0085` → subscribe common input `0x05`.
- Decode per the documented offsets. Print buttons, stick, gyro at 1 Hz plus rolling reports/second.
- **Dump raw frames to a file** — this becomes the golden-vector fixture for Phase 2. Don't skip it.

**Open questions this spike answers:**

1. **Does Windows require OS-level pairing before GATT works?** CoreBluetooth connects to an
   unpaired peripheral. On Windows, `BluetoothLEDevice.FromBluetoothAddressAsync` against an
   unpaired device works for some peripherals and fails for others. If pairing is required, both the
   SYNC-button flow and the reconnect story change shape. Biggest unknown in the project.
2. **What cadence do we actually get?** Win11 caps BLE around 70 Hz; macOS measured 66–67. Should
   land in the same place.
3. **Can two Joy-Con 2s sustain 66 Hz each, simultaneously, on one radio?** This is new — upstream's
   Joy-Con 2 support is explicitly single-controller, so there is no prior evidence. Two concurrent
   BLE connections share radio time and Windows gives us little scheduling control. **If dual mode
   degrades both streams badly, that is a scope decision you'll want to make early, not after the
   UI is built.** Test this in the spike with both Joy-Cons, before Phase 2 commits to a device model.
4. **How badly does the Windows stack handle sleep/wake and range-loss reconnect?** macOS's backoff
   timings will not transfer.

**Exit criteria:** ≥60 decoded reports/second sustained 5 minutes on a single Joy-Con; a
dual-connection cadence measurement (pass or fail, but measured); clean reconnect after sleep/wake;
raw frame dump captured.

### Phase 2 — Core logic port

C# port, with tests, of the platform-independent core.

| Swift | LOC | C# target |
| --- | ---: | --- |
| `JoyCon2BLEProtocol.swift` | 319 | `Core/Protocol/JoyCon2Protocol.cs` |
| `GyroProcessor.swift` | 511 | `Core/Gyro/GyroProcessor.cs` |
| `JoyConButtonMapping.swift` | 484 | `Core/Mapping/` |
| `ButtonAction.swift` | 705 | `Core/Mapping/` (trim macOS-only actions) |
| `InputEngine+JoyCon.swift` | 339 | `Core/Engine/` |
| `InputEngine.swift` | 729 | `Core/Engine/` (partial — strip Sense/G502X paths) |

Two things make this port trustworthy rather than hopeful:

- **Golden vectors.** The Phase 1 frame dump becomes a committed fixture. Assert the C# decoder
  produces identical output for every captured frame. This turns "does the decoder work" into a
  test you run locally in one command, instead of a debugging conversation across messages.
- **Port upstream's tests first.** `JoyCon2BLEInputDeviceBackendTests.swift` already covers
  protocol, cadence, decoder, and backend contract. Translate to xUnit before writing implementation.

Constants that must not drift: gyro scale `0.0608506463 °/s/count`; the 150-count neutral-correction
radius; nominal stick center 2048.

`ButtonAction` needs an honest audit rather than transliteration — Task View is `Win+Tab`, media
keys are `VK_MEDIA_*`, browser nav is `VK_BROWSER_BACK`/`FORWARD`. The default profile should be
re-derived for Windows.

#### Device model (revised for dual support)

Your answer to question 4 changes the shape here, so it's worth getting right up front:

```
JoyCon2Unit          one physical controller: one BLE session, one IMU, one stick, one LED
  ↓
IInputSource         logical device the engine binds to
  ├── SingleSource   one unit, held sideways/one-handed  (today's upstream behavior)
  └── DualSource     two units fused into one logical device
```

`DualSource` raises four questions that don't exist in single mode:

- **Which IMU drives the cursor?** Right by default, user-selectable, or fused. Fusing two IMUs with
  independent clocks is a real signal-processing problem — recommend "pick one" for v1.
- **Timestamp alignment.** Two units notify independently at ~15 ms intervals with no shared clock.
  The engine needs a policy: emit on either unit's frame using the other's last-known state
  (simple, low latency, slight staleness) or wait to pair frames (adds up to a full interval of
  latency). Recommend the former.
- **Partial connection.** One unit drops. Does the pair degrade to single mode, or pause? Needs a
  defined state machine, not emergent behavior.
- **Persistence.** Which two physical units constitute a pair, remembered across restarts.

`JamCon.Core` should be written against `IInputSource` from the start. Retrofitting dual support
after the engine assumes one device is the expensive path — which is why this was worth deciding now.

### Phase 3 — Mouse and keyboard output → *first usable milestone*

`MouseController.swift` (336 LOC) → `SendInput` P/Invoke.

- **Pointer ballistics.** Windows applies mouse acceleration ("Enhance pointer precision") and the
  system speed slider to `MOUSEEVENTF_MOVE`, which will distort the tuned gyro curve and make
  sensitivity tuning unfalsifiable. Use `MOUSEEVENTF_ABSOLUTE` against virtual-desktop coordinates
  and own the cursor position outright. Requires `SM_CXVIRTUALSCREEN` handling for multi-monitor.
- **Sub-pixel accumulation.** `SendInput` takes integers. Carry the fractional remainder between
  calls or slow precise gyro motion vanishes.
- **Scroll:** `MOUSEEVENTF_WHEEL` / `HWHEEL`, `WHEEL_DELTA = 120`, same accumulation concern.
- **Keyboard:** prefer `KEYEVENTF_SCANCODE` over virtual keys — many games read scancodes directly
  and ignore VK injection.
- **Timing:** `CACurrentMediaTime()` → `Stopwatch.GetTimestamp()`. Call `timeBeginPeriod(1)` and
  raise the input thread priority; .NET's default timer granularity adds jitter that the gyro
  smoothing will faithfully render as cursor wobble.
- **Permissions:** no Accessibility-permission equivalent, which is a genuine simplification. But
  UIPI blocks injection into elevated windows unless JamCon runs elevated. Document it; don't
  silently request admin.

### Phase 4 — ViGEm virtual gamepad

Second output mode behind an `IOutputSink` abstraction so mouse and gamepad are swappable.

- `Nefarius.ViGEm.Client` (MIT) + the ViGEmBus kernel driver.
- **Risk:** ViGEmBus was archived in November 2023 over a trademark conflict. It still works and
  DS4Windows still ships on it, but it gets no updates and the successor ("VirtualPad") has no
  release. The interface is the mitigation.
- Requires a driver install — meaningfully worse UX than mouse mode, and another reason mouse ships
  first.
- Dual mode maps cleanly to a full X360 pad. Single mode does not — decide whether a sideways
  Joy-Con exposes a partial pad or is simply unavailable in gamepad mode.
- Optional: HidHide to suppress the raw device so games see only the virtual pad.

### Phase 5 — UI → *first release*

- **WPF + H.NotifyIcon** for the tray. WinUI 3 is more modern but its tray and single-instance story
  is awkward for exactly this shape of app.
- Screens: Devices (incl. single/dual pairing), Buttons, Mouse Control, Debug.
- **Defer the radial menu.** `RadialMenuView.swift` is 541 LOC of transparent click-through overlay.
  On Windows that's `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST` — doable, but it's its own
  project and it isn't load-bearing for a first release.
- Settings: JSON under `%LOCALAPPDATA%\JamCon.Win\`, mirroring `SettingsStore`'s lock-protected
  snapshot handed to the engine thread.

### Phase 6 — Packaging

- Self-contained single-file `.exe`. MSIX only if Store distribution matters.
- Code signing — unsigned input-injection tools get hit hard by SmartScreen and AV heuristics.
- winget manifest as the Windows analogue of the deleted Homebrew cask.

## 5. Risk register

| Risk | Impact | Mitigation |
| --- | --- | --- |
| Windows requires pairing for GATT, and pairing is flaky with Joy-Con 2 | Blocks everything | Phase 1 spike; read Switch2Connect's approach |
| Two concurrent BLE connections can't both sustain ~66 Hz | Kills dual mode | Measure in Phase 1 **before** the device model is built |
| Windows BLE reconnect behavior differs from CoreBluetooth | Unstable in daily use | Re-derive backoff empirically; don't port macOS timings |
| C# decoder silently diverges from validated Swift decoder | Subtle wrong input, very hard to debug | Golden-vector fixture from the Phase 1 dump |
| Pointer ballistics distort tuned gyro curves | Cursor feels wrong; tuning unfalsifiable | Absolute positioning |
| ViGEmBus archived, no successor released | Gamepad mode rots | `IOutputSink` abstraction; mouse mode ships independently |
| Dual-IMU fusion turns into a research project | Scope blowout | v1 picks one IMU; no fusion |
| Deleting Swift loses the validated reference | Port has nothing to check against | `macos-reference` tag + temporary `reference/swift/` |

## 6. Sequencing and what it costs

The "half a day" figures in rev 1 were unfounded — I wrote them as if estimating a human developer's
wall-clock time, which is both a number I had no basis for and the wrong unit for what you're
actually spending. Removed.

Here is the honest version. The cost driver is **round trips**, because per Section 3 I can't
compile or test any of this. Rough shape:

| Phase | Round trips | Why |
| --- | --- | --- |
| 0 — fork and gut | Low | Mostly deletion and scaffolding. Deterministic, little to get wrong. |
| 1 — BLE spike | **Low from me, high from you** | I write the probe once. The expensive part is you running it against hardware and reporting behavior. Unknown *duration*, but not credit-hungry — most of the cost is Bluetooth being Bluetooth. |
| 2 — core port | **Highest** | ~2,400 LOC of translation plus tests, and I can't run `dotnet test`. Golden vectors are what keep this from spiraling. |
| 3 — SendInput output | Medium | Small surface, but P/Invoke signatures are easy to get subtly wrong and only hardware reveals it. |
| 5 — tray UI | Medium | WPF XAML is verbose; mistakes are usually obvious at build time. |
| 4 — ViGEm | Low-medium | Small, well-documented API surface. |
| 6 — packaging | Low | Mostly config. |

Two ways to cut the bill substantially, both worth doing:

1. **Run Claude Code locally on Windows for Phase 2 onward.** It can execute `dotnet build` and
   `dotnet test` itself, so compile-error loops cost nothing here. Use this session for design and
   for the large protocol-critical chunks.
2. **Batch your feedback.** One message containing full build output beats five messages of
   individual errors.

Order of work:

```
Phase 0  fork and gut
Phase 1  BLE spike                    ← gate; includes the dual-connection cadence test
Phase 2  core port + golden vectors
Phase 3  SendInput                    ← first usable build
Phase 5  minimal tray UI + settings   ← first release
Phase 4  ViGEm gamepad mode           second release
Phase 6  signing, winget              alongside first release
```

Phase 1 is the only phase whose difficulty is genuinely unknown. Everything after it is estimable.
