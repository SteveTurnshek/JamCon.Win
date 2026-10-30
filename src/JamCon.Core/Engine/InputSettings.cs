using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Engine;

/// <summary>
/// Everything the engine reads, as one immutable snapshot. Ported from SettingsStore.InputSettings,
/// reduced to what a Joy-Con 2 uses.
/// </summary>
public sealed record InputSettings
{
    public const double DefaultJoystickScrollSpeed = 8.0;
    public const double DefaultJoystickScrollAcceleration = 3.0;

    public static InputSettings Default { get; } = new();

    /// <summary>Master switch for all output. Turning it off releases anything held down.</summary>
    public bool IsEnabled { get; init; } = true;

    public GyroSettings Gyro { get; init; } = GyroSettings.Default;

    public ButtonMappingProfile RightButtons { get; init; } = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right);
    public ButtonMappingProfile LeftButtons { get; init; } = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Left);

    /// <summary>Whether gyro and stick may move the cursor or scroll. Buttons still work when off.</summary>
    public bool CursorControlEnabled { get; init; } = true;

    public bool JoystickScrollEnabled { get; init; } = true;
    public double JoystickScrollSpeed { get; init; } = DefaultJoystickScrollSpeed;
    public double JoystickScrollAcceleration { get; init; } = DefaultJoystickScrollAcceleration;

    public RadialMenuConfiguration RadialMenu { get; init; } = RadialMenuConfiguration.Default;

    public ButtonMappingProfile ButtonsFor(JoyCon2Handedness handedness) =>
        handedness == JoyCon2Handedness.Right ? RightButtons : LeftButtons;
}

/// <summary>
/// The one-way bridge from UI to engine: the UI replaces the snapshot, the engine reads it once per
/// frame. Reads are lock-free; writes are serialized.
/// </summary>
public sealed class SettingsStore(InputSettings initial)
{
    private readonly object _writeGate = new();
    private InputSettings _current = initial;

    public SettingsStore() : this(InputSettings.Default)
    {
    }

    public event Action<InputSettings>? Changed;

    public InputSettings Snapshot() => Volatile.Read(ref _current);

    public void Update(Func<InputSettings, InputSettings> transform)
    {
        InputSettings next;
        lock (_writeGate)
        {
            next = transform(_current);
            Volatile.Write(ref _current, next);
        }

        Changed?.Invoke(next);
    }
}

/// <summary>Ported from NumericSettingNormalizer in SliderWithLabel.swift.</summary>
public static class NumericSettingNormalizer
{
    /// <summary>
    /// Clamps to the range and snaps to the step, measured from the lower bound.
    /// Non-finite values become the lower bound.
    /// </summary>
    /// <remarks>
    /// Halfway values round away from zero, matching Swift's <c>rounded()</c>. C#'s default
    /// banker's rounding would silently snap some values to a different step.
    /// </remarks>
    public static double Normalize(double value, double min, double max, double step)
    {
        if (!double.IsFinite(value)) return min;
        var clamped = Math.Clamp(value, min, max);
        if (!double.IsFinite(step) || step <= 0) return clamped;

        var steps = Math.Round((clamped - min) / step, MidpointRounding.AwayFromZero);
        return Math.Clamp(min + steps * step, min, max);
    }
}
