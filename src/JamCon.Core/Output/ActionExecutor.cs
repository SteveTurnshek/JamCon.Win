using JamCon.Core.Mapping;

namespace JamCon.Core.Output;

/// <summary>
/// Executes button actions through the <see cref="SyntheticOutputCoordinator"/>. Ported from
/// ActionExecutor.swift, with system actions re-derived for Windows.
/// </summary>
/// <remarks>
/// Every Windows system action is an ordinary key combination, so all of them go through the
/// coordinator as taps. Upstream needed separate paths for Launchpad (opening an app bundle) and
/// media keys (NSEvent system-defined events); Windows needs neither.
/// </remarks>
public sealed class ActionExecutor
{
    private readonly SyntheticOutputCoordinator _coordinator;
    private readonly Action<ButtonAction, EngineActionPhase, SyntheticOutputOwner>? _engineAction;

    /// <param name="engineAction">
    /// Runs the actions the engine owns rather than the coordinator: switching modes and levelling
    /// the view (acted on at press), and virtual Xbox buttons (press, release and tap).
    /// </param>
    public ActionExecutor(
        Action<SyntheticOutputEvent> post, Action<ButtonAction, EngineActionPhase, SyntheticOutputOwner>? engineAction = null)
    {
        _coordinator = new SyntheticOutputCoordinator(post);
        _engineAction = engineAction;
    }

    public SyntheticOutputCoordinator Coordinator => _coordinator;

    public void Execute(ButtonAction action, bool isPressed, SyntheticOutputOwner owner)
    {
        switch (action)
        {
            case MouseClickAction click:
                _coordinator.Set(SyntheticOutput.Mouse(click.Button), isPressed, owner);
                break;
            case KeyPressAction key:
                _coordinator.Set(SyntheticOutput.Key(key.Combo), isPressed, owner);
                break;
            case SystemShortcutAction system when isPressed:
                PerformSystemAction(system.Action, owner);
                break;

            // As a plain press, the hold-only kinds behave as one tap; their held behaviour is the
            // engine's, since it needs the release and a timer.
            case HoldToggleAction toggle when isPressed:
                _coordinator.Tap(SyntheticOutput.Key(toggle.Combo), owner);
                break;
            case RepeatKeyAction repeat when isPressed:
                _coordinator.Tap(SyntheticOutput.Key(repeat.Combo), owner);
                break;
            case ToggleModeAction or LevelViewAction when isPressed:
                _engineAction?.Invoke(action, EngineActionPhase.Press, owner);
                break;
            case GamepadButtonAction:
                _engineAction?.Invoke(action, isPressed ? EngineActionPhase.Press : EngineActionPhase.Release, owner);
                break;

            // None, and the gyro modes, which the engine's gyro routing handles.
        }
    }

    public void Tap(ButtonAction action, SyntheticOutputOwner owner)
    {
        switch (action)
        {
            case MouseClickAction click:
                _coordinator.Tap(SyntheticOutput.Mouse(click.Button), owner);
                break;
            case KeyPressAction key:
                _coordinator.Tap(SyntheticOutput.Key(key.Combo), owner);
                break;
            case HoldToggleAction toggle:
                _coordinator.Tap(SyntheticOutput.Key(toggle.Combo), owner);
                break;
            case RepeatKeyAction repeat:
                _coordinator.Tap(SyntheticOutput.Key(repeat.Combo), owner);
                break;
            case SystemShortcutAction system:
                PerformSystemAction(system.Action, owner);
                break;
            case ToggleModeAction or LevelViewAction:
                _engineAction?.Invoke(action, EngineActionPhase.Press, owner);
                break;
            case GamepadButtonAction:
                _engineAction?.Invoke(action, EngineActionPhase.Tap, owner);
                break;
        }
    }

    /// <summary>Release every synthetic down event. Call before stopping, disabling or losing a device.</summary>
    public void ReleaseAll() => _coordinator.ReleaseAll();

    public void ReleaseAll(string deviceId) => _coordinator.ReleaseAll(deviceId);

    public static KeyCombo ShortcutFor(SystemAction action) => action switch
    {
        SystemAction.TaskView => new KeyCombo(VirtualKeys.Tab, KeyModifiers.Win),
        SystemAction.StartMenu => new KeyCombo(0x5B), // VK_LWIN on its own
        SystemAction.ShowDesktop => new KeyCombo(VirtualKeys.D, KeyModifiers.Win),
        SystemAction.AppSwitcher => new KeyCombo(VirtualKeys.Tab, KeyModifiers.Control | KeyModifiers.Alt),
        SystemAction.Dictation => new KeyCombo(VirtualKeys.H, KeyModifiers.Win),
        _ => new KeyCombo(VirtualKeys.MediaPlayPause),
    };

    private void PerformSystemAction(SystemAction action, SyntheticOutputOwner owner) =>
        _coordinator.Tap(
            SyntheticOutput.Key(ShortcutFor(action)),
            owner.WithRole(SyntheticOutputRole.SystemAction, action.ToString()));
}
