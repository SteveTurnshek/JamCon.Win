# AGENTS.md

Guidance for coding agents working in this repository.

JamCon.Win is a **Windows-only** C#/.NET fork of [jturnshek/JamCon](https://github.com/jturnshek/JamCon),
supporting Switch 2 Joy-Con only. macOS support was removed, not maintained. If you find yourself
writing Swift, AppKit, CoreBluetooth or CGEvent code, you are in the wrong repository.

## Build and test

Requires the .NET 9 SDK and Windows 11 22H2+.

```bash
dotnet build JamCon.Win.sln
```

```bash
dotnet test JamCon.Win.sln
```

There is no script-based workflow. Upstream needed one because macOS accessibility permissions are
tied to code signatures; Windows has no equivalent requirement, which is a real simplification.

## The one architectural rule

**`JamCon.Core` targets plain `net9.0`. Never add a Windows TFM to it.**

| Project | TFM | Contents |
| --- | --- | --- |
| `src/JamCon.Core/` | `net9.0` | Protocol decode, gyro math, button mapping, engine policy |
| `src/JamCon.Bluetooth/` | `net9.0-windows10.0.22621.0` | WinRT GATT transport |
| `src/JamCon.Output/` | `net9.0-windows10.0.22621.0` | SendInput, ViGEm |
| `src/JamCon.App/` | `net9.0-windows10.0.22621.0` | WPF tray UI |
| `tools/Jc2Probe/` | `net9.0-windows10.0.22621.0` | BLE transport spike |
| `tests/JamCon.Core.Tests/` | `net9.0` | xUnit |

No WinRT, no P/Invoke and no UI types in `JamCon.Core`. It is the part that stays testable and
reviewable in isolation, and that property is load-bearing for the whole port. The Windows TFM is
defined once in `Directory.Build.props` as `$(JamConWindowsTfm)`; don't hardcode it per project.

Platform-specific code belongs behind an interface defined in Core. Output sinks go behind
`IOutputSink` specifically, because ViGEmBus is archived upstream and may need replacing.

## Ported constants must not drift

`docs/JoyCon2-Bluetooth.md` is the authoritative spec — hardware-validated on macOS, and the reason
this port is not a reverse-engineering project. Treat it as the contract.

Gyro scale `0.0608506463 °/s/count`, neutral-correction radius 150 counts, nominal stick center 2048.
These live in `JamCon.Core` and are guarded by `ConstantsDriftTests`. If you change one, you are
either fixing a transcription error against the spec or introducing a bug — be sure which.

When porting a decoder, verify against captured frames rather than by reading. A C# decoder that
diverges subtly from the validated Swift one produces plausible-but-wrong input that is very hard to
trace back.

## reference/swift/ is read-only

It holds upstream Swift files copied verbatim at the `macos-reference` tag, as port sources. Nothing
there is compiled and no C# should reference it. Delete the directory once the port is validated
against the golden-vector fixture. See `reference/swift/README.md` for the file-to-target mapping.

## Windows behaviors that will bite you

Recorded with evidence in `docs/Windows-Port.md`. Briefly:

- Use `MOUSEEVENTF_ABSOLUTE`, not `MOUSEEVENTF_MOVE`. Pointer ballistics distort relative motion and
  make gyro tuning unfalsifiable.
- `SendInput` takes integers — accumulate the sub-pixel remainder or slow gyro motion vanishes.
- Prefer `KEYEVENTF_SCANCODE` over virtual keys; many games ignore VK injection.
- Don't port macOS reconnect timings. Re-derive them empirically.
- Don't request elevation. UIPI blocks injection into elevated windows; that is documented, not
  worked around.
- Don't transliterate `ButtonAction`. Windows actions differ: Task View is `Win+Tab`, media keys are
  `VK_MEDIA_*`, browser navigation is `VK_BROWSER_BACK`/`FORWARD`.

## Planning

`docs/JamCon-Win-Plan.md` holds the phase breakdown, architecture decisions and risk register.
Decisions recorded there are settled; don't reopen them without a reason that gets written down.
Phase 1 (the BLE transport spike) gates everything after it, and only hardware can answer its open
questions — whether Windows requires OS-level pairing before GATT works, and whether two Joy-Con 2s
can sustain ~66 Hz each on one radio.

## Licensing

MIT, copyright (c) 2024-2025 James Turnshek, retained. Attribution to upstream is a legal obligation.
Any borrowed third-party code must be MIT-compatible and attributed.
