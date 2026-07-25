// Phase 1 BLE transport spike — not implemented yet.
//
// Planned scope (docs/JamCon-Win-Plan.md §4 Phase 1): scan advertisements for Nintendo
// manufacturer data, run the initialization sequence from docs/JoyCon2-Bluetooth.md verbatim,
// decode common input report 0x05, print buttons/stick/gyro at 1 Hz alongside a rolling
// reports/second figure, and dump raw frames to a file as the golden-vector fixture for Phase 2.
//
// Console only: no mapping, no mouse output, no UI.

Console.WriteLine("Jc2Probe: Phase 1 BLE transport spike is not implemented yet.");
Console.WriteLine("See docs/JamCon-Win-Plan.md and docs/Windows-Port.md for the open questions it answers.");
return 0;
