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
    private readonly IGamepadOutput? _gamepad;

    /// <summary>The device whose button opened the radial menu; only one menu is open at a time.</summary>
    private string? _radialOwner;
    private RadialMenuConfiguration? _radialConfig;
    private double _radialX;
    private double _radialY;

    public InputEngine(
        IOutputSink output,
        IHoldScheduler holdScheduler,
        Func<InputSettings> settings,
        IRadialMenuPresenter? radialMenu = null,
        IGamepadOutput? gamepad = null)
    {
        _gamepad = gamepad;
        _output = output;
        _holdScheduler = holdScheduler;
        _settings = settings;
        _radialMenu = radialMenu ?? NullRadialMenuPresenter.Instance;
        _executor = new ActionExecutor(output.Post, RunEngineAction);
    }

    /// <summary>Diagnostics: the gyro processor for a device, if connected.</summary>
    public GyroProcessor? GyroFor(string deviceId) => _devices.TryGetValue(deviceId, out var d) ? d.Gyro : null;

    /// <summary>Diagnostics: the live stick calibration for a device, if connected.</summary>
    public StickCalibration? StickCalibrationFor(string deviceId) =>
        _devices.TryGetValue(deviceId, out var d) ? d.Calibration : null;

    public int ActiveOutputCount => _executor.Coordinator.ActiveOutputCount;

    /// <summary>
    /// A <see cref="ToggleModeAction"/> fired. Raised on the engine thread; the host flips
    /// <see cref="InputSettings.Mode"/>, and the engine releases everything when it sees the change.
    /// </summary>
    public event Action? ModeToggleRequested;

    /// <summary>
    /// Net vertical look motion sent since game mode began or the view was last levelled, in mouse
    /// counts. Levelling sends it back.
    /// </summary>
    private double _pitchCounts;

    private void RunEngineAction(ButtonAction action, EngineActionPhase phase, SyntheticOutputOwner owner)
    {
        switch (action)
        {
            case ToggleModeAction:
                ModeToggleRequested?.Invoke();
                break;
            case LevelViewAction:
                if (_pitchCounts != 0) _output.Look(0, -_pitchCounts);
                _pitchCounts = 0;
                break;
            case GamepadButtonAction pad when owner.DeviceId is { } id && _devices.TryGetValue(id, out var device):
                switch (phase)
                {
                    case EngineActionPhase.Press:
                        device.PadPress(pad.Button);
                        break;
                    case EngineActionPhase.Release:
                        device.PadRelease(pad.Button);
                        break;
                    case EngineActionPhase.Tap:
                        // Held briefly: a game polls the pad, and a press and release inside one
                        // poll would never be seen.
                        device.PadPress(pad.Button);
                        var generation = device.PadGeneration;
                        _holdScheduler.Schedule(GamepadTapLength, () =>
                        {
                            if (device.PadGeneration != generation) return; // reset meanwhile
                            device.PadRelease(pad.Button);
                            SendGamepadIfActive();
                        });
                        break;
                }

                SendGamepadIfActive();
                break;
        }
    }

    private static readonly TimeSpan GamepadTapLength = TimeSpan.FromMilliseconds(60);

    /// <summary>Below this rotation speed, °/s, gyro leaves the right stick alone, so a resting controller never drifts the view.</summary>
    internal const double MinimumGyroStickSpeed = 1.5;

    /// <summary>The Joy-Con stick's own deadzone in Xbox controller mode, before the game applies its own.</summary>
    internal const double PadStickDeadzone = 0.08;

    private GamepadReport _lastPad = GamepadReport.Neutral;
    private bool _padActive;

    /// <summary>
    /// Xbox controller mode: the gyro of the aiming Joy-Con aims, through the right stick or the
    /// mouse, always or only while the left trigger is held.
    /// </summary>
    private void ProcessControllerGyro(DeviceState device, double dx, double dy, double dt, InputSettings s, bool aims)
    {
        device.GyroStick = (0, 0);
        if (!aims) return;
        if (s.ControllerGyroActivation == ControllerGyroActivation.WhileLeftTrigger && !AnyPadHeld(GamepadButtons.LeftTrigger)) return;

        if (s.ControllerGyroOutput == ControllerGyroOutput.Mouse)
        {
            _output.Look(dx, dy);
            return;
        }

        if (dt <= 0) return;

        // Back to degrees a second: the controller gyro defaults are linear, so the processor's
        // output is its sensitivity times the rotation.
        var countsPerDegree = Math.Max(0.01, s.ControllerGyro.Sensitivity * 0.1);
        var vx = dx / dt / countsPerDegree;
        var vy = dy / dt / countsPerDegree;
        var speed = Math.Sqrt(vx * vx + vy * vy);
        if (speed < MinimumGyroStickSpeed) return;

        // Start just past the game's deadzone, so a slow turn moves the view instead of vanishing in it.
        var deadzone = Math.Clamp(s.ControllerStickDeadzone, 0, 0.9);
        var magnitude = deadzone + (1 - deadzone) * Math.Min(1.0, speed / Math.Max(1.0, s.ControllerGyroFullSpeed));
        device.GyroStick = (magnitude * vx / speed, -magnitude * vy / speed); // XInput: positive Y is up
    }

    private static (double X, double Y) PadStick((byte X, byte Y) position)
    {
        var x = (position.X - 128.0) / 127.0;
        var y = (position.Y - 128.0) / 127.0; // positive is up, as XInput
        var length = Math.Sqrt(x * x + y * y);
        if (length <= PadStickDeadzone) return (0, 0);
        var scaled = Math.Min(1.0, (length - PadStickDeadzone) / (1 - PadStickDeadzone));
        return (x / length * scaled, y / length * scaled);
    }

    private bool AnyPadHeld(GamepadButtons button)
    {
        foreach (var d in _devices.Values)
        {
            if (d.Connected && (d.PadButtons & button) != 0) return true;
        }

        return false;
    }

    private void SendGamepadIfActive()
    {
        if (_settings().Mode == InputMode.Controller) SendGamepad(IsPaired());
    }

    /// <summary>
    /// Combines every connected Joy-Con into one Xbox controller: buttons from both, each stick on
    /// its own side when paired (a lone Joy-Con's stick is the left stick), and gyro on the right.
    /// </summary>
    private void SendGamepad(bool paired)
    {
        if (_gamepad is not { IsAvailable: true }) return;

        var buttons = GamepadButtons.None;
        double lx = 0, ly = 0, rx = 0, ry = 0;
        foreach (var d in _devices.Values)
        {
            if (!d.Connected) continue;
            buttons |= d.PadButtons;
            if (paired && d.Handedness == JoyCon2Handedness.Right)
            {
                rx += d.PadStick.X;
                ry += d.PadStick.Y;
            }
            else
            {
                lx += d.PadStick.X;
                ly += d.PadStick.Y;
            }

            rx += d.GyroStick.X;
            ry += d.GyroStick.Y;
        }

        (lx, ly) = ClampToCircle(lx, ly);
        (rx, ry) = ClampToCircle(rx, ry);
        var report = new GamepadReport(buttons, lx, ly, rx, ry);
        if (_padActive && report == _lastPad) return;

        _gamepad.Send(report);
        _lastPad = report;
        _padActive = true;
    }

    private static (double X, double Y) ClampToCircle(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length <= 1 ? (x, y) : (x / length, y / length);
    }

    /// <summary>Unplugs the virtual controller, so games see it only in Xbox controller mode.</summary>
    private void ReleaseGamepad()
    {
        if (!_padActive) return;
        _gamepad?.Release();
        _padActive = false;
        _lastPad = GamepadReport.Neutral;
    }

    private void Look(double dx, double dy)
    {
        _pitchCounts += dy;
        _output.Look(dx, dy);
    }

    /// <summary>Both a left and a right Joy-Con are connected: they act as one controller.</summary>
    private bool IsPaired()
    {
        bool left = false, right = false;
        foreach (var d in _devices.Values)
        {
            if (!d.Connected) continue;
            if (d.Handedness == JoyCon2Handedness.Right) right = true;
            else left = true;
        }

        return left && right;
    }

    private DeviceState? Partner(DeviceState device)
    {
        foreach (var d in _devices.Values)
        {
            if (d.Connected && d.Handedness != device.Handedness) return d;
        }

        return null;
    }

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
        device.LastFrameTime = null;
        device.Connected = true;
    }

    /// <summary>The link dropped. Releases everything the controller held.</summary>
    public void DeviceDisconnected(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;
        ResetTransientState(device);
        device.Connected = false;
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
        ReleaseGamepad();
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
        ReleaseGamepad();
        _devices.Clear();
    }

    public void ProcessFrame(string deviceId, JoyCon2Report report, double timestamp)
    {
        if (!_devices.TryGetValue(deviceId, out var device)) return;

        var motionSampleRate = device.Cadence.Record(timestamp);
        var dt = device.LastFrameTime is { } previous ? Math.Clamp(timestamp - previous, 0, 0.05) : 0;
        device.LastFrameTime = timestamp;

        // One zero-filled report is normal while the IMU starts; upstream drops it whole.
        if (report.Imu.IsZeroFilled) return;

        var s = _settings();
        if (!s.IsEnabled) return;
        if (s.Mode != InputMode.Controller) ReleaseGamepad();

        if (device.Mode != s.Mode)
        {
            // Nothing held in one mode may stay held in the other: a desktop drag or a game's W key
            // would otherwise be stuck down. Buttons still held re-prime and do not fire again.
            ResetTransientState(device, resetGyro: false);
            device.Mode = s.Mode;
            _pitchCounts = 0; // whatever the view is now counts as level
        }

        var game = s.Mode == InputMode.Game;
        var paired = IsPaired();
        var profile = s.ButtonsFor(device.Handedness, paired);

        // A pair aims with the right Joy-Con, as a Switch 2 game does; the left one's gyro is ignored.
        var aims = !paired || device.Handedness == JoyCon2Handedness.Right;
        var cursorEnabled = s.CursorControlEnabled;

        // Bounded neutral correction around the factory/connection anchor.
        var stick = StickReader.Raw(report, device.Handedness);
        device.Calibration.UpdateAutoCalibration(stick.X, stick.Y, timestamp);

        if (device.HasPrimedButtonState)
        {
            ProcessButtons(device, report.Buttons, profile, s);
        }
        else
        {
            // Buttons already held when a device (re)connects or output is re-enabled must not fire.
            PrimeButtons(device, report.Buttons);
            device.HasPrimedButtonState = true;
        }

        var axes = GyroRemapper.Remap(report.Imu.GyroX, report.Imu.GyroY, report.Imu.GyroZ, device.Handedness);
        var gyro = s.ActiveGyro with { ExpectedSampleRate = motionSampleRate };
        var scale = GyroSettings.EffectiveScale(gyro.GyroScale, GyroConstants.DegreesPerSecondPerCount);
        var (dx, dy) = device.Gyro.Process(axes.Pitch, axes.Yaw, axes.Roll, timestamp, gyro, scale);
        if (s.Mode == InputMode.Controller)
        {
            ProcessControllerGyro(device, dx, dy, dt, s, aims && cursorEnabled);
            device.PadStick = device.Calibration.IsCalibrated ? PadStick(StickReader.Normalized(stick, device.Calibration)) : (0, 0);
            SendGamepad(paired);
            return;
        }

        RouteGyroMovement(device, paired ? Partner(device) : null, dx, dy, cursorEnabled, profile.HasDragMapping, game, aims, paired);

        if (!cursorEnabled || !device.Calibration.IsCalibrated) return;
        if (game && paired && device.Handedness == JoyCon2Handedness.Right)
        {
            ProcessStickTurn(StickReader.Normalized(stick, device.Calibration), s, dt);
        }
        else if (game)
        {
            if (s.GameStickMovement) ProcessStickMovement(device, StickReader.Normalized(stick, device.Calibration));
        }
        else if (s.JoystickScrollEnabled)
        {
            ProcessJoystickScroll(StickReader.Normalized(stick, device.Calibration), s);
        }
    }

    /// <summary>Stick deflection, as a fraction of full travel, that presses a direction key.</summary>
    internal const double StickPressThreshold = 0.5;

    /// <summary>Lower than the press threshold, so a stick resting near it does not chatter the key.</summary>
    internal const double StickReleaseThreshold = 0.35;

    private static readonly (ushort Key, string Control)[] StickKeys =
    [
        (VirtualKeys.W, "stick.up"), (VirtualKeys.S, "stick.down"), (VirtualKeys.A, "stick.left"), (VirtualKeys.D, "stick.right"),
    ];

    /// <summary>Game mode: the stick holds W, A, S and D, diagonals pressing two at once.</summary>
    private void ProcessStickMovement(DeviceState device, (byte X, byte Y) position)
    {
        const double center = 128.0;
        const double maxDelta = 127.0;
        var x = (position.X - center) / maxDelta;
        var y = (position.Y - center) / maxDelta; // positive is up
        double[] deflection = [y, -y, -x, x];

        for (var i = 0; i < StickKeys.Length; i++)
        {
            var held = device.StickKeysHeld[i];
            var wanted = held ? deflection[i] > StickReleaseThreshold : deflection[i] > StickPressThreshold;
            if (wanted == held) continue;

            device.StickKeysHeld[i] = wanted;
            var (key, control) = StickKeys[i];
            _executor.Execute(
                ButtonAction.Key(key), wanted, new SyntheticOutputOwner(device.Id, control, SyntheticOutputRole.Press));
        }
    }

    private enum GyroMode { None, Normal, Drag, Scroll, RadialMenu }

    /// <summary>
    /// What this device's gyro does now. In a pair, a clutch held on either Joy-Con applies to the
    /// aiming one, so the left hand's drag or scroll button still works.
    /// </summary>
    private GyroMode CurrentGyroMode(DeviceState device, DeviceState? partner, bool hasDragMapping, bool aims, bool paired)
    {
        // The menu follows its opener's gyro, except in a pair, where the aiming hand steers it.
        var steersMenu = _radialConfig is not null && (paired ? aims : _radialOwner == device.Id);
        if (steersMenu) return GyroMode.RadialMenu;
        if (!aims || device.RadialMenuButtonHeld) return GyroMode.None;
        if (device.ScrollButtonHeld || partner is { ScrollButtonHeld: true }) return GyroMode.Scroll;
        if (device.DragButtonHeld || partner is { DragButtonHeld: true }) return GyroMode.Drag;
        return hasDragMapping ? GyroMode.None : GyroMode.Normal;
    }

    private void RouteGyroMovement(
        DeviceState device, DeviceState? partner, double dx, double dy, bool cursorEnabled, bool hasDragMapping, bool game, bool aims, bool paired)
    {
        switch (CurrentGyroMode(device, partner, hasDragMapping, aims, paired))
        {
            case GyroMode.Normal or GyroMode.Drag when cursorEnabled:
                if (game) Look(dx, dy);
                else _output.MoveCursor(dx, dy);
                break;
            case GyroMode.Scroll when cursorEnabled:
                _output.Scroll(dx, dy);
                break;
            case GyroMode.RadialMenu when _radialConfig is { } config:
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

    /// <summary>Below this deflection the right stick does not turn, so a resting stick never drifts the view.</summary>
    internal const double StickTurnDeadzone = 0.15;

    /// <summary>
    /// A pair in game mode: the right stick turns left and right, as in Splatoon's motion controls,
    /// for turns too big to make with the wrist. Up and down do nothing. The speed is in the same
    /// units as turning the controller, so a full push turns the view as a
    /// <see cref="InputSettings.GameStickTurnSpeed"/> °/s turn of the Joy-Con would.
    /// </summary>
    private void ProcessStickTurn((byte X, byte Y) position, InputSettings s, double dt)
    {
        var x = (position.X - 128.0) / 127.0;
        var magnitude = Math.Abs(x);
        if (magnitude <= StickTurnDeadzone || dt <= 0) return;

        var t = Math.Min(1.0, (magnitude - StickTurnDeadzone) / (1.0 - StickTurnDeadzone));
        var degreesPerSecond = Math.Sign(x) * t * t * s.GameStickTurnSpeed;
        var countsPerDegree = s.GameGyro.Sensitivity * 0.1;
        Look(degreesPerSecond * dt * countsPerDegree, 0);
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

    private void ProcessButtons(DeviceState device, uint buttons, ButtonMappingProfile profile, InputSettings timing)
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
                    if (isPressed) GestureDown(device, button, actions, timing);
                    else GestureUp(device, button, timing);
                }
                else if (isPressed)
                {
                    ButtonDown(device, button, actions, timing.HoldThreshold);
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

        // Mouse buttons go down immediately so a held click can drag. Keys wait to see if it is a
        // hold, unless there is no hold action to wait for: then they too follow the button, which
        // is what a game expects of a movement or sprint key.
        if (HeldWithButton(actions)) _executor.Execute(actions.Press, true, state.PressOwner);

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
        if (HeldWithButton(state.Actions))
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

    private static bool HeldWithButton(ButtonActions actions) =>
        actions.Press is MouseClickAction || (actions.Press is KeyPressAction or GamepadButtonAction && actions.Hold is NoAction);

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

    private void ResetTransientState(DeviceState device, bool resetGyro = true)
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
        Array.Clear(device.StickKeysHeld);
        device.ClearPad();
        if (_padActive) SendGamepad(IsPaired());

        // A mode switch keeps the learned gyro bias: the controller has not changed.
        if (resetGyro) device.Gyro.Reset();
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
    private void GestureDown(DeviceState device, JoyCon2Button button, ButtonActions actions, InputSettings timing)
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
            gesture.Timer = _holdScheduler.Schedule(TimeSpan.FromSeconds(timing.HoldThreshold), () =>
            {
                if (!ReferenceEquals(device.Gestures[index], second) || second.Phase != GesturePhase.SecondDown) return;
                second.Phase = GesturePhase.SecondHeld;
                BeginHeld(second.Actions.DoubleTapHold, second.DoubleOwner, second.Held);
            });
            return;
        }

        var first = new Gesture(actions, device.Id, button.ToString()) { Phase = GesturePhase.FirstDown };
        device.Gestures[index] = first;
        first.Timer = _holdScheduler.Schedule(TimeSpan.FromSeconds(timing.HoldThreshold), () =>
        {
            if (!ReferenceEquals(device.Gestures[index], first) || first.Phase != GesturePhase.FirstDown) return;
            first.Phase = GesturePhase.FirstHeld;
            BeginHeld(first.Actions.Hold, first.HoldOwner, first.Held);
        });
    }

    private void GestureUp(DeviceState device, JoyCon2Button button, InputSettings timing)
    {
        var index = (int)button;
        if (device.Gestures[index] is not { } gesture) return;
        gesture.Timer?.Cancel();

        switch (gesture.Phase)
        {
            case GesturePhase.FirstDown:
                gesture.Phase = GesturePhase.AwaitingSecond;
                gesture.Timer = _holdScheduler.Schedule(TimeSpan.FromSeconds(timing.DoubleTapWindow), () =>
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
        public bool[] StickKeysHeld { get; } = new bool[4];
        public InputMode Mode { get; set; }
        public bool Connected { get; set; }
        public double? LastFrameTime { get; set; }

        /// <summary>How many of this device's buttons hold each Xbox button, by bit position.</summary>
        private readonly int[] _padCounts = new int[18];

        public GamepadButtons PadButtons { get; private set; }

        /// <summary>Bumped on every reset, so a pending tap release from before it does nothing.</summary>
        public int PadGeneration { get; private set; }

        public (double X, double Y) PadStick { get; set; }
        public (double X, double Y) GyroStick { get; set; }

        public void PadPress(GamepadButtons buttons) => CountPad(buttons, +1);

        public void PadRelease(GamepadButtons buttons) => CountPad(buttons, -1);

        public void ClearPad()
        {
            Array.Clear(_padCounts);
            PadButtons = GamepadButtons.None;
            PadStick = GyroStick = (0, 0);
            PadGeneration++;
        }

        private void CountPad(GamepadButtons buttons, int delta)
        {
            for (var bit = 0; bit < _padCounts.Length; bit++)
            {
                var flag = (GamepadButtons)(1u << bit);
                if ((buttons & flag) == 0) continue;
                _padCounts[bit] = Math.Max(0, _padCounts[bit] + delta);
                PadButtons = _padCounts[bit] > 0 ? PadButtons | flag : PadButtons & ~flag;
            }
        }
    }
}
