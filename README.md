# JamCon.Win

Use Nintendo Switch 2 Joy-Con controllers as a gyro mouse and keyboard on Windows.

JamCon.Win is a Windows-only fork of [jturnshek/JamCon](https://github.com/jturnshek/JamCon), a
macOS menu-bar app by James Turnshek. This fork drops macOS support entirely and rebuilds the
application in C#/.NET for Windows, targeting Switch 2 Joy-Con only.

> **Status: pre-alpha, not usable yet.** The solution scaffold is in place; the Bluetooth transport
> spike has not yet run against hardware. There is nothing to install. See
> [docs/JamCon-Win-Plan.md](docs/JamCon-Win-Plan.md) for the phase plan.

## Credit to upstream

The hard part of this project was already solved upstream. [docs/JoyCon2-Bluetooth.md](docs/JoyCon2-Bluetooth.md)
is a complete, hardware-validated Joy-Con 2 BLE specification — GATT service and characteristic
UUIDs, the exact initialization sequence, the `0x05` common-report byte layout, the gyro scaling
constant, and a written record of which alternate approaches were tried and rejected. That document,
and the working Swift implementation behind it, are the foundation this port builds on.

JamCon is MIT licensed, and this fork stays MIT licensed with James Turnshek's copyright intact.
See [LICENSE](LICENSE).

**If you use macOS, use [upstream JamCon](https://github.com/jturnshek/JamCon)** — it supports
original Joy-Con, Joy-Con 2, and PS VR2 Sense controllers and is actively maintained. This fork does
not track upstream and will not be merged back. The pre-fork Swift implementation is preserved here
at the `macos-reference` tag.

## Scope

| | |
| --- | --- |
| **Operating system** | Windows 11 22H2 or later |
| **Controllers** | Switch 2 Joy-Con only — left, right, or both as one device |
| **Not supported** | Windows 10, macOS, original Joy-Con, Pro Controller, PS VR2 Sense, Logitech G502X |

Windows 10 is excluded deliberately rather than merely untested: its Bluetooth LE stack caps
notification throughput near 20 Hz, and gyro mouse control at 20 Hz is unusable. Windows 11 is
expected to sustain roughly 60–70 Hz, matching what upstream measures on macOS — a figure this fork
will confirm or correct once the transport spike runs.

Original Joy-Con, Pro Controller, Sense, and G502X support existed upstream through macOS-only APIs
(IOKit HID, GameController, HID++). It was removed rather than ported.

## Planned features

- Gyro mouse control with adaptive smoothing, ported from upstream's tuned implementation
- Button mapping to clicks, keyboard shortcuts, and Windows system actions
- Single Joy-Con held sideways, or two Joy-Con fused into one logical device
- Virtual Xbox gamepad output via ViGEm, as a second output mode
- System tray application with per-device profiles

## Building

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and Windows 11 22H2+.

```bash
dotnet build JamCon.Win.sln
```

```bash
dotnet test JamCon.Win.sln
```

## Layout

```
src/JamCon.Core/          net9.0                        protocol, gyro, mapping, engine
src/JamCon.Bluetooth/     net9.0-windows10.0.22621.0    WinRT GATT transport
src/JamCon.Output/        net9.0-windows10.0.22621.0    SendInput, ViGEm
src/JamCon.App/           net9.0-windows10.0.22621.0    WPF tray UI
tools/Jc2Probe/           net9.0-windows10.0.22621.0    BLE transport spike
tests/JamCon.Core.Tests/  net9.0                        xUnit
reference/swift/                                        upstream port sources (temporary)
```

`JamCon.Core` targets plain `net9.0`, not `net9.0-windows`. It contains no WinRT, no P/Invoke, and no
UI — only protocol decode, gyro math, button mapping, and engine policy. This keeps the bulk of the
logic testable on any platform and confines the Windows-specific surface to four projects.

`reference/swift/` holds the upstream Swift files being ported. It sits alongside the C# during the
port and is deleted once the port is validated against captured hardware data.

## Documentation

- [Joy-Con 2 Bluetooth profile](docs/JoyCon2-Bluetooth.md) — inherited from upstream; the protocol spec
- [Windows port plan](docs/JamCon-Win-Plan.md) — architecture decisions, phases, risk register
- [Windows port findings](docs/Windows-Port.md) — Windows-specific behavior as it gets measured
- [Security policy](SECURITY.md) · [Code of conduct](CODE_OF_CONDUCT.md) · [Contributing](CONTRIBUTING.md)

## Disclaimer

Nintendo, Joy-Con, and Nintendo Switch are trademarks of Nintendo. Xbox is a trademark of Microsoft.
JamCon.Win is not affiliated with or endorsed by Nintendo or Microsoft.

## License

MIT. Copyright (c) 2024-2025 James Turnshek for the original work; Windows port changes under the
same license. See [LICENSE](LICENSE).
