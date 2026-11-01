# JamCon.Win

Use Nintendo Switch 2 Joy-Con controllers as a gyro mouse and keyboard on Windows.

JamCon.Win is a Windows-only fork of [jturnshek/JamCon](https://github.com/jturnshek/JamCon), a
macOS menu-bar app by James Turnshek. This fork drops macOS support entirely and rebuilds the
application in C#/.NET for Windows, targeting Switch 2 Joy-Con only.

> **Status: early, but working.** A right Joy-Con 2 connects from the system tray and works as a
> gyro mouse and keyboard. There is no installer yet; build it from source.
> Tested on one machine with one controller so far. See [docs/Windows-Port.md](docs/Windows-Port.md)
> for what has been measured and [docs/JamCon-Win-Plan.md](docs/JamCon-Win-Plan.md) for the plan.

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
notification throughput near 20 Hz, and gyro mouse control at 20 Hz is unusable. On Windows 11 a
Joy-Con 2 measured 66.8 reports per second sustained over five minutes, the same rate upstream
measures on macOS.

Original Joy-Con, Pro Controller, Sense, and G502X support existed upstream through macOS-only APIs
(IOKit HID, GameController, HID++). It was removed rather than ported.

## Using it

1. Build, then run `src/JamCon.App/bin/Release/net9.0-windows10.0.22621.0/JamCon.App.exe` once. It
   adds a **JamCon** shortcut to your Start menu, so after that you can start it by pressing Windows
   and typing "jamcon". A Joy-Con icon appears in the notification area (look under the ^ arrow).
2. **Do not pair the Joy-Con in Windows Bluetooth settings.** JamCon connects to it directly;
   a Windows pairing stops it working. If you already added it there, remove it.
3. Hold **SYNC** on the Joy-Con until its lights chase. The tray dot turns green when connected.
   JamCon remembers the controller. Whenever it has disconnected (it slept, went out of range, or
   JamCon restarted), hold SYNC again to reconnect: ordinary button presses do not bring it back.
4. **Hold X and point the controller to move the cursor.** Release X and the cursor stays put,
   so a resting controller never drifts it.

Avoid HOME: it wakes a Switch 2 if one is nearby, and the controller reconnects to the console.
Avoid SYNC once connected: it ends the connection.

Default buttons for a right Joy-Con 2:

| Button | Press | Hold |
| --- | --- | --- |
| ZR | Left click | |
| R | Right click | |
| + | Middle click | |
| X | Move cursor while held | |
| B | Radial menu: hold, point at a slice, release | |
| A | Esc | Ctrl+C |
| Y | See below | Dictate while held |
| HOME | Task View | Play/Pause |
| Stick click | Browser back | Browser forward |
| SL / SR | Down / Up arrow | |
| C | | Next mode |
| Stick | Scroll | |

**Y is the dictation button**, built for a dictation app whose shortcut is Ctrl+Space in toggle mode
(such as Handy): hold Y to dictate (JamCon presses Ctrl+Space as the hold starts and again when you
let go), double tap Y for Enter, and tap then hold Y to delete a word every half second
(Ctrl+Backspace) until you let go. A single tap does nothing. Dictation starts 0.3 s into the hold.

The radial menu's default slices are the four arrow keys, as upstream, but turned 45° so each one
is centered on its direction (upstream put Up between 12 and 3 o'clock). While it is open the cursor
holds still and a dot follows the controller instead. Its slices, an optional outer ring, rotation
and sizes are editable in settings, with a live preview.

After 10 minutes without use, JamCon disconnects so the Joy-Con can sleep and save its battery.
Hold SYNC to wake it. Right-click the tray icon to
put it to sleep sooner, turn **Mouse control** off (anything held is released at once), recalibrate
the gyro, or open the log folder. It also has a live status window and log.

**Left-click the tray icon for settings:** cursor speed and smoothing for each mode (plus upstream's
advanced acceleration and filter controls), Xbox controller aiming, scrolling, hold and double-tap
timing, the sleep delay, smooth motion, and starting with Windows. From there, **Edit buttons**
remaps every button's tap, hold, double-tap and tap-then-hold actions for each mode, alone or in a
pair, and **Edit radial menu** changes the menu. Changes apply at once. Smooth motion re-emits the
controller's ~67 Hz motion at 240 Hz for smooth movement on high-refresh displays, at a cost of
about 7.5 ms of latency. Everything is stored in `%LOCALAPPDATA%\JamCon.Win\settings.json`.

## Modes

**Hold C** (on a left Joy-Con, Capture) to step through three modes: **desktop**, **mouse & keyboard
game**, and **Xbox controller**. A label on screen confirms which.

### Mouse & keyboard game mode

The gyro always aims, sent as relative mouse movement that games read
as mouse look. There is no X clutch and no acceleration, so a given turn always turns the camera by
the same amount. Set the in-game mouse sensitivity, then fine-tune **Aim speed** in settings. Keys
are held for as long as their buttons are.

| Button | Game mode |
| --- | --- |
| ZR / R | Left / right click (fire / aim) |
| Stick | W A S D |
| Stick click | Shift |
| A / B | Space / Ctrl |
| X / Y | R / E |
| SL / SR | Q / F |
| + | Esc |
| C (hold) | Next mode |

### Both Joy-Cons

Hold SYNC on each Joy-Con; JamCon remembers one of each and uses them as one controller. In
desktop mode each keeps its own buttons, and only the right one's gyro moves the cursor (a clutch
held on either hand works). In game mode the pair plays like a Switch 2 game with motion controls,
such as Splatoon:

| Button | Game mode, both Joy-Cons |
| --- | --- |
| Right gyro | Aim |
| Left stick | W A S D |
| Right stick | Turn left / right (speed in settings) |
| Y | Level the view, like Splatoon's camera reset |
| ZR / R | Left / right click |
| B / A / X | Space / E / R |
| ZL / L | Ctrl / Q |
| Left stick click / right stick click | Shift / V |
| D-pad | 1 2 3 4 |
| + / − | Esc / Tab |
| C or Capture (hold) | Next mode |

Levelling the view undoes the up/down aim JamCon has sent since the last level (or since game mode
began). It is approximate: motion the game stopped at its look limit, or from a real mouse, is not
counted.

### Xbox controller mode

Games see a real Xbox controller (through the [ViGEmBus](https://github.com/nefarius/ViGEmBus)
driver, which must be installed; without it this mode is skipped), plus gyro aiming. The virtual
controller is plugged in only while you are in this mode.

- **Gyro aims through the right stick** by default, which works in every game. Settings tune how fast a
  turn pushes the stick and compensate for the game's stick deadzone. For games that accept mouse
  and controller together, gyro can aim through the mouse instead, which is more precise.
- **Gyro is always on** by default, or only while ZL (left trigger) is held, as in shooters that
  enable gyro when aiming down sights.
- **Face buttons map by position**, as on an Xbox pad: the bottom button (B) is Xbox A, A is B, Y is X,
  X is Y.
- **Both Joy-Cons**: each stick is its own side's, L/R are the bumpers, ZL/ZR the triggers, + and −
  are Start and Back, the d-pad is the d-pad, and stick clicks are stick clicks. The right gyro aims.
- **One Joy-Con**: its stick is the left stick and its gyro aims. ZR/R are the right trigger and bumper
  (ZL/L on a left Joy-Con), SL/SR the left trigger and bumper, + (or −) is Start, and a tap of C (or
  Capture) is Back. A left Joy-Con's d-pad stands in for the face buttons.

HOME is never mapped: it wakes a nearby Switch 2.

## Features

Working:

- Gyro mouse with upstream's tuned processing: adaptive One Euro smoothing, automatic drift
  calibration, an acceleration curve, and absolute cursor positioning unaffected by Windows pointer
  acceleration
- Buttons mapped to clicks, keys and Windows actions, with separate tap and hold actions
- Stick scrolling
- Upstream's radial menu
- Smooth 240 Hz cursor and scroll output from the controller's ~67 Hz reports
- Automatic reconnection to a remembered controller, and idle sleep to save its battery
- Game mode: gyro mouse look, stick as W A S D, held game keys; with both Joy-Cons, Splatoon-style aim, turn and level view
- Xbox controller mode (ViGEmBus) with gyro aiming through the right stick or the mouse
- Settings window with button remapping, a radial menu editor and advanced gyro tuning
- Start menu shortcut, and optional start with Windows
- System tray app with live status and a log

Planned:

- Hardware testing of two Joy-Cons together
- An installer

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
