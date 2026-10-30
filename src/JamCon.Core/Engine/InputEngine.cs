using JamCon.Core.Diagnostics;
using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Output;
using JamCon.Core.Protocol;

namespace JamCon.Core.Engine;

/// <summary>
/// Turns decoded Joy-Con 2 reports into cursor motion, clicks and keys.
/// </summary>
/// <remarks>
/// <para>
/// Ported from InputEngine.swift and InputEngine+JoyCon.swift, reduced to the Joy-Con 2 path. Per
/// frame: update the stick's neutral calibration, run the press/hold button state machine, remap
/// and process the gyro, route the result to the cursor or to scrolling, then stick scrolling.
/// </para>
/// <para>
/// The invariant every reset path protects: nothing JamCon pressed may stay pressed. Disconnects,
/// disabling output and stopping all release held outputs before discarding button state, because
/// the physical release report for a held button may never arrive.
/// </para>
/// <para>
/// Not thread-safe. Every call, including hold-timer callbacks, must arrive on one thread;
/// <see cref="EngineHost"/> provides that thread.
/// </para>
/// </remarks>
public sealed class InputEngine
{
    private readonly IOutputSink _output;
    private readonly IHoldScheduler _holdScheduler;
    private readonly Func<InputSettings> _settings;
    private readonly ActionExecutor _executor;
    private readonly Dictionary<string, DeviceState> _devices = new();
    private readonly IRadialMenuPresenter _radialMenu;

    /// <summary>The device whose button opened the radial menu; only one menu is open at a time.</summary>
    private string? _radialOwner;
    private RadialMenuConfiguration? _radialConfig;
    private double _radialX;
    private double _radialY;

    public InputEngine(
        IOutputSink output, IHoldScheduler holdScheduler, Func<InputSettings> settings, IRadialMenuPresenter? radialMenu = null)
    {
        _output = output;
        _holdScheduler = holdScheduler;
        _settings = settings;
        _radialMenu = radialMenu ?? NullRadialMenuPresenter.Instance;
        _executor = new ActionExecutor(output.Post);
    }

    /// <summary>Diagnostics: the gyro processor for a device, if connected.</summary>
    public GyroProcessor? GyroFor(string deviceId) => _devices.TryGetValue(deviceId, out var d) ? d.Gyro : null;

    /// <summary>Diagnostics: the live stick calibration for a device, if connected.</summary>
    public StickCalibration? StickCalibrationFor(string deviceId) =>
        _devices.TryGetValue(deviceId, out var d) ? d.Calibration : null;

    public int ActiveOutputCount => _executor.Coordinator.ActiveOutputCount;

    /// <summary>
    /// A controller connected, or reconnected. Starts it from a clean slate and anchors its stick on
    /// the factory record when one was read.
    /// </summary>
    public void DeviceConnected(string deviceId, JoyCon2Handedness handedness, JoyCon2StickCalibration? factoryCalibration)
    {
        if (_devices.TryGetValue(deviceId, out var existing))
        {
            ResetTransientState(existing);
            if (existing.Handedness != handedness) _devices.Remove(deviceId);
        }

        if (!_devices.TryGetValue(deviceId, out var device))
        {
            device = new DeviceState(deviceId, handedness);
            _devices[deviceId] = device;
        }

        device.Calibration.Reset(requireNeutralValidation: device.RequiresNeutralAfterReconnect);
        device.RequiresNeutralAfterReconnect = false;
        if (factoryCalibration is { } calibration) device.Calibration.ApplyHardwareCalibration(calibration);
        device.Cadence.Reset();
    }

    /// <summary>The link dropped. Releases everything the controller held.</summary>
    public void DeviceDisconnected(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var device)) ResetTransientState(device);
    }

    /// <summary>
    /// Applies the master switch synchronously, so a key or mouse button can never stay down while
    /// the later physical release report is ignored.
    /// </summary>
    public void SetInputEnabled(bool enabled)
    {
        if (enabled) return;
        foreach (var device in _devices.Values) ResetTransientState(device);
        _executor.ReleaseAll();
    }

    /// <summary>Re-learns gyro bias from scratch.</summary>
    public void Recalibrate()
    {
        foreach (var device in _devices.Values) device.Gyro.Reset();
    }

    /// <summary>
    /// A user-requested recovery: like a reconnect, but stick output waits for a fresh neutral window
    /// before resuming.
    /// </summary>
    public void ResetDevice(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;
        ResetTransientState(device);
        device.RequiresNeutralAfterReconnect = true;
        device.Calibration.Reset(requireNeutralValidation: true);
    }

    public void Stop()
    {
        foreach (var device in _devices.Values) ResetTransientState(device);
        _executor.ReleaseAll();
        _devices.Clear();
    }

    public void ProcessFrame(string deviceId, JoyCon2Report report, double timestamp)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        var motionSampleRate = device.Cadence.Record(timestamp);

        // One zero-filled report is normal while the IMU starts; upstream drops it whole.
        if (report.Imu.IsZeroFilled) return;

        var s = _settings();
        if (!s.IsEnabled) return;

        var profile = s.ButtonsFor(device.Handedness);
        var cursorEnabled = s.CursorControlEnabled;

        // Bounded neutral correction around the factory/connection anchor.
        var stick = StickReader.Raw(report, device.Handedness);
        device.Calibration.UpdateAutoCalibration(stick.X, stick.Y, timestamp);

        if (device.HasPrimedButtonState)
        {
            ProcessButtons(device, report.Buttons, profile);
        }
        else
        {
            // Buttons already held when a device (re)connects or output is re-enabled must not fire.
            PrimeButtons(device, report.Buttons);
            device.HasPrimedButtonState = true;
        }

        var axes = GyroRemapper.Remap(report.Imu.GyroX, report.Imu.GyroY, report.Imu.GyroZ, device.Handedness);
        var gyro = s.Gyro with { ExpectedSampleRate = motionSampleRate };
        var scale = GyroSettings.EffectiveScale(gyro.GyroScale, GyroConstants.DegreesPerSecondPerCount);
        var (dx, dy) = device.Gyro.Process(axes.Pitch, axes.Yaw, axes.Roll, timestamp, gyro, scale);
        RouteGyroMovement(device, dx, dy, cursorEnabled, profile.HasDragMapping);

        if (s.JoystickScrollEnabled && cursorEnabled && device.Calibration.IsCalibrated)
        {
            ProcessJoystickScroll(StickReader.Normalized(stick, device.Calibration), s);
        }
    }

    private enum GyroMode { None, Normal, Drag, Scroll, RadialMenu }

    private static GyroMode CurrentGyroMode(DeviceState device, bool hasDragMapping)
    {
        if (device.RadialMenuButtonHeld) return GyroMode.RadialMenu;
        if (device.ScrollButtonHeld) return GyroMode.Scroll;
        if (device.DragButtonHeld) return GyroMode.Drag;
        return hasDragMapping ? GyroMode.None : GyroMode.Normal;
    }

    private void RouteGyroMovement(DeviceState device, double dx, double dy, bool cursorEnabled, bool hasDragMapping)
    {
        switch (CurrentGyroMode(device, hasDragMapping))
        {
            case GyroMode.Normal or GyroMode.Drag when cursorEnabled:
                _output.MoveCursor(dx, dy);
                break;
            case GyroMode.Scroll when cursorEnabled:
                _output.Scroll(dx, dy);
                break;
            case GyroMode.RadialMenu when _radialOwner == device.Id && _radialConfig is { } config:
                // Gyro steers the menu's dot instead of the cursor, which holds still meanwhile.
                var scale = Math.Max(0.1, config.RadialMovementScale);
                var (x, y, selection) = RadialMenuGeometry.Resolve(_radialX + dx * scale, _radialY + dy * scale, config);
                _radialX = x;
                _radialY = y;
                _radialMenu.Update(x, y, selection);
                break;

            // None: a drag clutch is mapped but not held, so gyro motion does nothing.
        }
    }

    /// <summary>Ported from beginRadialMenu. The first device to open the menu owns it until release.</summary>
    private void OpenRadialMenu(DeviceState device)
    {
        _radialOwner ??= device.Id;
        if (_radialOwner != device.Id) return;

        device.RadialMenuButtonHeld = true;
        _radialX = 0;
        _radialY = 0;

        // Captured at open, so the menu that runs is the one that was drawn even if settings change.
        _radialConfig = _settings().RadialMenu;
        _radialMenu.Show(_radialConfig);
    }

    private void CloseRadialMenu(RadialMenuItem? selected)
    {
        _radialMenu.Hide(selected);
        _radialOwner = null;
        _radialConfig = null;
    }

    /// <summary>Ported from processJoyConJoystickScroll. Stick up scrolls up.</summary>
    /// <remarks>
    /// Like upstream's Joy-Con path, the amount is per report rather than per second, so scroll speed
    /// scales with report rate. Upstream's Sense path corrects for this with a frame-time scale;
    /// the Joy-Con path never did. Kept as-is so the tuned speed carries over unchanged.
    /// </remarks>
    private void ProcessJoystickScroll((byte X, byte Y) position, InputSettings s)
    {
        const double deadzone = 20.0;
        const double center = 128.0;
        const double maxDelta = 127.0;

        var deltaX = position.X - center;
        var deltaY = -(position.Y - center);
        if (Math.Abs(deltaX) <= deadzone && Math.Abs(deltaY) <= deadzone) return;

        double Scaled(double delta)
        {
            var sign = delta >= 0 ? 1.0 : -1.0;
            var normalized = Math.Min(1.0, Math.Abs(delta) / maxDelta);
            var curved = normalized * normalized;
            var accelGain = 1.0 + (s.JoystickScrollAcceleration - 1.0) * normalized;
            return sign * curved * accelGain * s.JoystickScrollSpeed * 2.0;
        }

        _output.Scroll(Scaled(deltaX), Scaled(deltaY));
    }

    private void ProcessButtons(DeviceState device, uint buttons, ButtonMappingProfile profile)
    {
        foreach (var button in JoyCon2ButtonLayout.AvailableButtons(device.Handedness))
        {
            var index = (int)button;
            var isPressed = JoyCon2ButtonLayout.IsPressed(buttons, button, device.Handedness);
            var wasPressed = device.PreviousButtons[index];

            if (isPressed != wasPressed)
            {
                var actions = profile.ActionsFor(button);

                // A gesture already under way finishes as it began, even if the mapping changed mid-way.
                if (device.Gestures[index] is not null || (isPressed && actions.UsesDoubleTap))
                {
                    if (isPressed) GestureDown(device, button, actions, profile);
                    else GestureUp(device, button, profile);
                }
                else if (isPressed)
                {
                    ButtonDown(device, button, actions, profile.HoldThreshold);
                }
                else
                {
                    ButtonUp(device, button, profile);
                }
            }

            device.PreviousButtons[index] = isPressed;
        }
    }

    private void PrimeButtons(DeviceState device, uint buttons)
    {
        foreach (var button in JoyCon2ButtonLayout.AvailableButtons(device.Handedness))
        {
            var index = (int)button;
            device.PreviousButtons[index] = JoyCon2ButtonLayout.IsPressed(buttons, button, device.Handedness);
            device.PressStates[index]?.HoldTimer?.Cancel();
            device.PressStates[index] = null;
            device.Gestures[index]?.Timer?.Cancel();
            device.Gestures[index] = null;
        }
    }

    private void ButtonDown(DeviceState device, JoyCon2Button button, ButtonActions actions, double holdThreshold)
    {
        var index = (int)button;
        var state = new PressState(actions, device.Id, button.ToString());
        device.PressStates[index] = state;

        if (actions.PressIsGyroMode)
        {
            switch (actions.Press)
            {
                case DragAction: device.DragButtonHeld = true; break;
                case ScrollAction: device.ScrollButtonHeld = true; break;
                case RadialMenuAction: OpenRadialMenu(device); break;
            }

            return;
        }

        // Mouse buttons go down immediately so a held click can drag; keys wait to see if it is a hold.
        if (actions.Press is MouseClickAction) _executor.Execute(actions.Press, true, state.PressOwner);

        if (actions.Hold is not NoAction)
        {
            state.HoldTimer = _holdScheduler.Schedule(TimeSpan.FromSeconds(holdThreshold), () =>
            {
                // Validate against the live state: the press may have ended, or been replaced by a
                // new press, while this callback was queued.
                if (!ReferenceEquals(device.PressStates[index], state) || state.HoldFired) return;
                state.HoldFired = true;
                BeginHeld(state.Actions.Hold, state.HoldOwner, state.Held);
            });
        }
    }

    private void ButtonUp(DeviceState device, JoyCon2Button button, ButtonMappingProfile profile)
    {
        var index = (int)button;
        var state = device.PressStates[index];
        state?.HoldTimer?.Cancel();

        if (state is { Actions.PressIsGyroMode: true })
        {
            ReleaseGyroMode(device, state.Actions.Press);
            device.PressStates[index] = null;
            return;
        }

        var current = profile.ActionsFor(button);
        if (current.PressIsGyroMode)
        {
            ReleaseGyroMode(device, current.Press);
            return;
        }

        if (state is null) return;

        // Release the actions captured at button-down, so editing the mapping mid-press cannot
        // leave the original output stuck down.
        if (state.Actions.Press is MouseClickAction)
        {
            _executor.Execute(state.Actions.Press, false, state.PressOwner);
            if (state.HoldFired) EndHeld(state.Actions.Hold, state.HoldOwner, state.Held);
        }
        else if (state.HoldFired)
        {
            EndHeld(state.Actions.Hold, state.HoldOwner, state.Held);
        }
        else if (state.Actions.Press is not NoAction)
        {
            _executor.Tap(state.Actions.Press, state.PressOwner);
        }

        device.PressStates[index] = null;
    }

    private void ReleaseGyroMode(DeviceState device, ButtonAction action)
    {
        switch (action)
        {
            case DragAction: device.DragButtonHeld = false; break;
            case ScrollAction: device.ScrollButtonHeld = false; break;
            case RadialMenuAction:
                device.RadialMenuButtonHeld = false;
                if (_radialOwner != device.Id || _radialConfig is not { } config) break;

                // Releasing selects whatever slice the dot is in; in the center, nothing.
                var selection = RadialMenuGeometry.Resolve(_radialX, _radialY, config).Selection;
                var item = RadialMenuGeometry.ItemFor(selection, config);
                CloseRadialMenu(item);
                if (item is not null)
                {
                    _executor.Tap(item.Action, new SyntheticOutputOwner(device.Id, "radialMenu.selection", SyntheticOutputRole.RadialMenu));
                }

                break;
        }
    }

    private void ResetTransientState(DeviceState device)
    {
        foreach (var state in device.PressStates)
        {
            if (state is null) continue;
            state.HoldTimer?.Cancel();
            _executor.Execute(state.Actions.Press, false, state.PressOwner);
            if (state.HoldFired) EndHeld(state.Actions.Hold, state.HoldOwner, state.Held);
        }

        // Ending held gestures matters beyond releasing keys: a hold-toggle must send its closing
        // tap, or a dictation app would be left recording after a disconnect.
        foreach (var gesture in device.Gestures)
        {
            if (gesture is null) continue;
            gesture.Timer?.Cancel();
            if (gesture.Phase == GesturePhase.FirstHeld) EndHeld(gesture.Actions.Hold, gesture.HoldOwner, gesture.Held);
            if (gesture.Phase == GesturePhase.SecondHeld) EndHeld(gesture.Actions.DoubleTapHold, gesture.DoubleOwner, gesture.Held);
        }

        Array.Clear(device.Gestures);

        // A menu open on a device that is going away closes without running anything.
        if (_radialOwner == device.Id) CloseRadialMenu(null);

        Array.Clear(device.PressStates);
        Array.Clear(device.PreviousButtons);
        device.HasPrimedButtonState = false;
        device.DragButtonHeld = device.ScrollButtonHeld = device.RadialMenuButtonHeld = false;
        device.Gyro.Reset();
        _executor.ReleaseAll(device.Id);
    }

    /// <summary>
    /// Starts a held action. Keys and clicks go down; a hold-toggle taps its key; a repeating key taps
    /// at once and then on a timer until <see cref="EndHeld"/>.
    /// </summary>
    private void BeginHeld(ButtonAction action, SyntheticOutputOwner owner, HeldAction held)
    {
        held.Active = true;
        switch (action)
        {
            case HoldToggleAction toggle:
                _executor.Tap(toggle, owner);
                break;
            case RepeatKeyAction repeat:
                _executor.Tap(repeat, owner);
                ScheduleRepeat(repeat, owner, held);
                break;
            default:
                _executor.Execute(action, true, owner);
                break;
        }
    }

    private void ScheduleRepeat(RepeatKeyAction repeat, SyntheticOutputOwner owner, HeldAction held)
    {
        held.Repeat = _holdScheduler.Schedule(TimeSpan.FromSeconds(Math.Max(0.05, repeat.IntervalSeconds)), () =>
        {
            if (!held.Active) return;
            _executor.Tap(repeat, owner);
            ScheduleRepeat(repeat, owner, held);
        });
    }

    /// <summary>Ends a held action begun by <see cref="BeginHeld"/>. Safe to call twice.</summary>
    private void EndHeld(ButtonAction action, SyntheticOutputOwner owner, HeldAction held)
    {
        if (!held.Active) return;
        held.Active = false;
        held.Repeat?.Cancel();
        switch (action)
        {
            case HoldToggleAction toggle:
                _executor.Tap(toggle, owner);
                break;
            case RepeatKeyAction:
                break;
            default:
                _executor.Execute(action, false, owner);
                break;
        }
    }

    /// <summary>
    /// First press: wait to see whether it is a hold (run Hold) or a short press. After a short
    /// press, wait up to the double-tap window for a second press: none runs Press, a short second
    /// press runs DoubleTap, a held second press runs DoubleTapHold. See <see cref="ButtonActions"/>.
    /// </summary>
    private void GestureDown(DeviceState device, JoyCon2Button button, ButtonActions actions, ButtonMappingProfile profile)
    {
        var index = (int)button;
        var gesture = device.Gestures[index];

        if (gesture is { Phase: GesturePhase.AwaitingSecond })
        {
            gesture.Timer?.Cancel();
            if (gesture.Actions.DoubleTapHold is NoAction)
            {
                // Nothing to wait for: act on the second press at once.
                _executor.Tap(gesture.Actions.DoubleTap, gesture.DoubleOwner);
                gesture.Phase = GesturePhase.SecondTapped;
                return;
            }

            gesture.Phase = GesturePhase.SecondDown;
            var second = gesture;
            gesture.Timer = _holdScheduler.Schedule(TimeSpan.FromSeconds(profile.HoldThreshold), () =>
            {
                if (!ReferenceEquals(device.Gestures[index], second) || second.Phase != GesturePhase.SecondDown) return;
                second.Phase = GesturePhase.SecondHeld;
                BeginHeld(second.Actions.DoubleTapHold, second.DoubleOwner, second.Held);
            });
            return;
        }

        var first = new Gesture(actions, device.Id, button.ToString()) { Phase = GesturePhase.FirstDown };
        device.Gestures[index] = first;
        first.Timer = _holdScheduler.Schedule(TimeSpan.FromSeconds(profile.HoldThreshold), () =>
        {
            if (!ReferenceEquals(device.Gestures[index], first) || first.Phase != GesturePhase.FirstDown) return;
            first.Phase = GesturePhase.FirstHeld;
            BeginHeld(first.Actions.Hold, first.HoldOwner, first.Held);
        });
    }

    private void GestureUp(DeviceState device, JoyCon2Button button, ButtonMappingProfile profile)
    {
        var index = (int)button;
        if (device.Gestures[index] is not { } gesture) return;
        gesture.Timer?.Cancel();

        switch (gesture.Phase)
        {
            case GesturePhase.FirstDown:
                gesture.Phase = GesturePhase.AwaitingSecond;
                gesture.Timer = _holdScheduler.Schedule(TimeSpan.FromSeconds(profile.DoubleTapWindow), () =>
                {
                    if (!ReferenceEquals(device.Gestures[index], gesture) || gesture.Phase != GesturePhase.AwaitingSecond) return;
                    device.Gestures[index] = null;
                    if (gesture.Actions.Press is not NoAction) _executor.Tap(gesture.Actions.Press, gesture.PressOwner);
                });
                return;

            case GesturePhase.FirstHeld:
                EndHeld(gesture.Actions.Hold, gesture.HoldOwner, gesture.Held);
                break;

            case GesturePhase.SecondDown:
                _executor.Tap(gesture.Actions.DoubleTap, gesture.DoubleOwner);
                break;

            case GesturePhase.SecondHeld:
                EndHeld(gesture.Actions.DoubleTapHold, gesture.DoubleOwner, gesture.Held);
                break;
        }

        device.Gestures[index] = null;
    }

    /// <summary>A held action in progress, and its repeat timer if it has one.</summary>
    private sealed class HeldAction
    {
        public bool Active { get; set; }
        public IHoldTimer? Repeat { get; set; }
    }

    private enum GesturePhase { FirstDown, FirstHeld, AwaitingSecond, SecondDown, SecondHeld, SecondTapped }

    /// <summary>A double-tap-capable press sequence, with the actions captured at its first press.</summary>
    private sealed class Gesture(ButtonActions actions, string deviceId, string control)
    {
        public ButtonActions Actions { get; } = actions;
        public GesturePhase Phase { get; set; }
        public IHoldTimer? Timer { get; set; }
        public HeldAction Held { get; } = new();
        public SyntheticOutputOwner PressOwner { get; } = new(deviceId, control, SyntheticOutputRole.Press);
        public SyntheticOutputOwner HoldOwner { get; } = new(deviceId, control, SyntheticOutputRole.Hold);
        public SyntheticOutputOwner DoubleOwner { get; } = new(deviceId, control, SyntheticOutputRole.DoubleTap);
    }

    private sealed class PressState(ButtonActions actions, string deviceId, string control)
    {
        public ButtonActions Actions { get; } = actions;
        public SyntheticOutputOwner PressOwner { get; } = new(deviceId, control, SyntheticOutputRole.Press);
        public SyntheticOutputOwner HoldOwner { get; } = new(deviceId, control, SyntheticOutputRole.Hold);
        public bool HoldFired { get; set; }
        public IHoldTimer? HoldTimer { get; set; }
        public HeldAction Held { get; } = new();
    }

    private sealed class DeviceState(string id, JoyCon2Handedness handedness)
    {
        public string Id { get; } = id;
        public JoyCon2Handedness Handedness { get; } = handedness;
        public GyroProcessor Gyro { get; } = new();
        public StickCalibration Calibration { get; } = new();

        /// <summary>Measured report rate, fed to the gyro processor as its expected rate.</summary>
        public CadenceEstimator Cadence { get; } = new(fallbackRateHz: 66.0);

        public bool HasPrimedButtonState { get; set; }
        public bool RequiresNeutralAfterReconnect { get; set; }
        public bool[] PreviousButtons { get; } = new bool[JoyCon2ButtonLayout.ButtonCount];
        public PressState?[] PressStates { get; } = new PressState?[JoyCon2ButtonLayout.ButtonCount];
        public Gesture?[] Gestures { get; } = new Gesture?[JoyCon2ButtonLayout.ButtonCount];
        public bool DragButtonHeld { get; set; }
        public bool ScrollButtonHeld { get; set; }
        public bool RadialMenuButtonHeld { get; set; }
    }
}
