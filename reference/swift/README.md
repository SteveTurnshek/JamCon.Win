# Upstream Swift port sources — temporary

These files are copied verbatim from upstream [jturnshek/JamCon](https://github.com/jturnshek/JamCon)
at the `macos-reference` tag. They are **read-only port sources**, not part of the build. Nothing in
this directory is compiled, and no C# should reference it.

They sit here so the Phase 2 port can be checked against the validated implementation without
constant git archaeology. **Delete this directory once the port is validated against the Phase 1
golden-vector fixture.**

MIT licensed, copyright (c) 2024-2025 James Turnshek. See [../../LICENSE](../../LICENSE).

| File | Upstream path | Ports to |
| --- | --- | --- |
| `JoyCon2BLEProtocol.swift` | `Sources/JamCon/Devices/` | `Core/Protocol/JoyCon2Protocol.cs` |
| `JoyCon2BLESession.swift` | `Sources/JamCon/Devices/` | `JamCon.Bluetooth` (WinRT GATT session) |
| `JoyCon2BLEInputDeviceBackend.swift` | `Sources/JamCon/Devices/` | `JamCon.Bluetooth` backend |
| `GyroProcessor.swift` | `Sources/JamCon/` | `Core/Gyro/GyroProcessor.cs` |
| `JoyConButtonMapping.swift` | `Sources/JamCon/` | `Core/Mapping/` |
| `ButtonAction.swift` | `Sources/JamCon/` | `Core/Mapping/` — audit for Windows, don't transliterate |
| `InputEngine+JoyCon.swift` | `Sources/JamCon/Engine/` | `Core/Engine/` |
| `InputEngine.swift` | `Sources/JamCon/Engine/` | `Core/Engine/` — partial; strip Sense/G502X paths |
| `MouseController.swift` | `Sources/JamCon/` | `JamCon.Output` (SendInput) |
| `JoyCon2BLEInputDeviceBackendTests.swift` | `Tests/JamConTests/` | `tests/JamCon.Core.Tests/` — translate before implementing |

`ButtonAction.swift` needs an honest Windows audit rather than a transliteration: Task View is
`Win+Tab`, media keys are `VK_MEDIA_*`, browser navigation is `VK_BROWSER_BACK`/`FORWARD`. The
default profile should be re-derived for Windows rather than mapped one-to-one from macOS.
