using System.Text.Json;
using JamCon.Core.Engine;
using JamCon.Core.Gyro;
using JamCon.Core.Mapping;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

public class ButtonLayoutTests
{
    /// <summary>Every bit below was verified on Windows with a right Joy-Con 2 (docs/Windows-Port.md).</summary>
    [Theory]
    [InlineData(JoyCon2Button.Y, 0)]
    [InlineData(JoyCon2Button.X, 1)]
    [InlineData(JoyCon2Button.B, 2)]
    [InlineData(JoyCon2Button.A, 3)]
    [InlineData(JoyCon2Button.SR, 4)]
    [InlineData(JoyCon2Button.SL, 5)]
    [InlineData(JoyCon2Button.R, 6)]
    [InlineData(JoyCon2Button.ZR, 7)]
    [InlineData(JoyCon2Button.Plus, 9)]
    [InlineData(JoyCon2Button.StickClick, 10)]
    [InlineData(JoyCon2Button.C, 14)]
    public void RightButtonsMatchHardwareVerifiedBits(JoyCon2Button button, int bit)
    {
        Assert.Equal(bit, JoyCon2ButtonLayout.BitFor(button, JoyCon2Handedness.Right));
    }

    /// <summary>From upstream's testJoyCon2UsesIndependentProfileAndExposesCButtonOnRightOnly.</summary>
    [Fact]
    public void CButtonExistsOnRightOnly()
    {
        Assert.Contains(JoyCon2Button.C, JoyCon2ButtonLayout.AvailableButtons(JoyCon2Handedness.Right));
        Assert.DoesNotContain(JoyCon2Button.C, JoyCon2ButtonLayout.AvailableButtons(JoyCon2Handedness.Left));

        // Upstream sets report byte 5 bit 6; that is bit 14 of the four-byte field.
        var report = new byte[0x3F];
        report[5] = 1 << 6;
        Assert.True(JoyCon2ReportDecoder.TryDecode(report.AsSpan(0, 0x3F), out var decoded));
        Assert.True(JoyCon2ButtonLayout.IsPressed(decoded.Buttons, JoyCon2Button.C, JoyCon2Handedness.Right));
    }

    /// <summary>
    /// Byte 0x07 reads 0xE0 at rest on real hardware. None of those bits may map to a button, or the
    /// engine would see three permanently held buttons.
    /// </summary>
    [Fact]
    public void StatusByteBitsNeverReadAsButtons()
    {
        const uint statusOnly = 0xE0000000;
        foreach (var side in new[] { JoyCon2Handedness.Right, JoyCon2Handedness.Left })
        {
            foreach (var button in JoyCon2ButtonLayout.AvailableButtons(side))
            {
                Assert.False(JoyCon2ButtonLayout.IsPressed(statusOnly, button, side), $"{side} {button}");
            }
        }
    }

    [Fact]
    public void NoTwoButtonsOnOneSideShareABit()
    {
        foreach (var side in new[] { JoyCon2Handedness.Right, JoyCon2Handedness.Left })
        {
            var bits = JoyCon2ButtonLayout.AvailableButtons(side).Select(b => JoyCon2ButtonLayout.BitFor(b, side)).ToArray();
            Assert.Equal(bits.Length, bits.Distinct().Count());
            Assert.All(bits, b => Assert.InRange(b!.Value, 0, 23));
        }
    }
}

public class DefaultProfileTests
{
    /// <summary>
    /// Adapted from upstream's testCanonicalJoyConDefaultsMirrorOneHandedSemanticsAcrossGenerations,
    /// with macOS actions replaced by their Windows equivalents.
    /// </summary>
    [Fact]
    public void RightDefaultsKeepUpstreamSemanticsWithWindowsActions()
    {
        var profile = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right);

        Assert.Equal(new ButtonActions(ButtonAction.Click(MouseButton.Left)), profile.ActionsFor(JoyCon2Button.ZR));
        Assert.Equal(new ButtonActions(ButtonAction.Click(MouseButton.Right)), profile.ActionsFor(JoyCon2Button.R));
        Assert.Equal(new ButtonActions(ButtonAction.Click(MouseButton.Middle)), profile.ActionsFor(JoyCon2Button.Plus));
        Assert.Equal(new ButtonActions(ButtonAction.Drag), profile.ActionsFor(JoyCon2Button.X));
        Assert.Equal(
            new ButtonActions(ButtonAction.Key(VirtualKeys.BrowserBack), ButtonAction.Key(VirtualKeys.BrowserForward)),
            profile.ActionsFor(JoyCon2Button.StickClick));
        Assert.Equal(
            new ButtonActions(ButtonAction.System(SystemAction.TaskView), ButtonAction.System(SystemAction.PlayPause)),
            profile.ActionsFor(JoyCon2Button.Home));
        Assert.Equal(ButtonActions.Empty, profile.ActionsFor(JoyCon2Button.C));
        Assert.Equal(ButtonActions.Empty, profile.ActionsFor(JoyCon2Button.B));
        Assert.Equal(ButtonMappingProfile.DefaultHoldThreshold, profile.HoldThreshold);
        Assert.True(profile.HasDragMapping);
    }

    [Fact]
    public void LeftDefaultsMirrorRight()
    {
        var profile = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Left);

        Assert.Equal(new ButtonActions(ButtonAction.Click(MouseButton.Left)), profile.ActionsFor(JoyCon2Button.ZL));
        Assert.Equal(new ButtonActions(ButtonAction.Click(MouseButton.Right)), profile.ActionsFor(JoyCon2Button.L));
        Assert.Equal(new ButtonActions(ButtonAction.Click(MouseButton.Middle)), profile.ActionsFor(JoyCon2Button.Minus));
        Assert.Equal(new ButtonActions(ButtonAction.Drag), profile.ActionsFor(JoyCon2Button.DpadUp));
        Assert.Equal(
            new ButtonActions(ButtonAction.System(SystemAction.TaskView), ButtonAction.System(SystemAction.PlayPause)),
            profile.ActionsFor(JoyCon2Button.Capture));
    }

    [Fact]
    public void ScrollDefaultsMatchUpstream()
    {
        Assert.Equal(8.0, InputSettings.DefaultJoystickScrollSpeed);
        Assert.Equal(3.0, InputSettings.DefaultJoystickScrollAcceleration);
        Assert.Equal(InputSettings.DefaultJoystickScrollSpeed, new InputSettings().JoystickScrollSpeed);
    }

    [Fact]
    public void SettingAGyroModePressClearsTheHold()
    {
        var profile = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right).WithPress(JoyCon2Button.A, ButtonAction.Scroll);

        Assert.Equal(new ButtonActions(ButtonAction.Scroll), profile.ActionsFor(JoyCon2Button.A));
        Assert.True(profile.HasScrollMapping);
    }

    [Fact]
    public void ProfileRoundTripsThroughJson()
    {
        var original = ButtonMappingProfile.DefaultFor(JoyCon2Handedness.Right)
            .WithPress(JoyCon2Button.C, ButtonAction.Key(VirtualKeys.Space, KeyModifiers.Control | KeyModifiers.Shift))
            .WithHoldThreshold(0.45);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ButtonMappingProfile>(json)!;

        Assert.Equal(0.45, restored.HoldThreshold);
        foreach (var button in Enum.GetValues<JoyCon2Button>())
        {
            Assert.Equal(original.ActionsFor(button), restored.ActionsFor(button));
        }

        Assert.True(restored.HasDragMapping);
    }
}

public class GyroRemapperTests
{
    /// <summary>From upstream's testJoyCon2RightUsesMeasuredGripAxesWithoutChangingOriginalJoyCon.</summary>
    [Fact]
    public void RightUsesMeasuredGripAxes()
    {
        Assert.Equal(new GyroAxes(Pitch: 100, Yaw: 300, Roll: 200), GyroRemapper.Remap(100, 200, 300, JoyCon2Handedness.Right));
    }

    [Fact]
    public void LeftIsOriginalLeftMappingWithPitchAndYawNegated()
    {
        // Original left: (pitch: -y, yaw: z, roll: x). Joy-Con 2 negates pitch and yaw.
        Assert.Equal(new GyroAxes(Pitch: 200, Yaw: -300, Roll: 100), GyroRemapper.Remap(100, 200, 300, JoyCon2Handedness.Left));
    }

    [Fact]
    public void NegatingTheMinimumValueDoesNotWrap()
    {
        Assert.Equal(32768, GyroRemapper.Remap(0, 0, short.MinValue, JoyCon2Handedness.Left).Yaw);
    }
}

public class StickNeutralCorrectionTests
{
    /// <summary>From upstream's testLongHeldScrollPositionCannotWalkStickCenter.</summary>
    [Fact]
    public void LongHeldScrollPositionCannotWalkStickCenter()
    {
        var calibration = new StickCalibration();
        var t = 0.0;
        for (var i = 0; i < 45; i++, t += 1.0 / 66.0) calibration.UpdateAutoCalibration(2050, 2040, t);

        Assert.True(calibration.IsCalibrated);
        var learnedX = calibration.CenterX;
        var learnedY = calibration.CenterY;

        for (var i = 0; i < 66 * 60 * 5; i++, t += 1.0 / 66.0) calibration.UpdateAutoCalibration(2300, 2040, t);

        Assert.Equal(learnedX, calibration.CenterX, 0.001);
        Assert.Equal(learnedY, calibration.CenterY, 0.001);
        Assert.True(StickReader.Normalized(new StickSample(2300, 2040), calibration).X > 148);

        var released = StickReader.Normalized(new StickSample(2050, 2040), calibration);
        Assert.Equal((byte)128, released.X);
        Assert.Equal((byte)128, released.Y);
    }

    /// <summary>From upstream's testManualResetRequiresFactoryNeutralBeforeStickOutputResumes.</summary>
    [Fact]
    public void ManualResetRequiresFactoryNeutralBeforeStickOutputResumes()
    {
        var hardware = new JoyCon2StickCalibration(2050, 2040, 1500, 1600, 1400, 1300);
        var calibration = new StickCalibration();
        calibration.ApplyHardwareCalibration(hardware);
        Assert.True(calibration.IsCalibrated);

        calibration.Reset(requireNeutralValidation: true);
        calibration.ApplyHardwareCalibration(hardware);
        Assert.False(calibration.IsCalibrated);

        var t = 0.0;
        for (var i = 0; i < 66 * 60 * 5; i++, t += 1.0 / 66.0) calibration.UpdateAutoCalibration(2300, 2040, t);
        Assert.False(calibration.IsCalibrated);
        Assert.Equal(hardware.CenterX, calibration.CenterX);

        for (var i = 0; i < 45; i++, t += 1.0 / 66.0) calibration.UpdateAutoCalibration(hardware.CenterX, hardware.CenterY, t);
        Assert.True(calibration.IsCalibrated);
    }

    [Fact]
    public void FactoryRangesAreAuthoritative()
    {
        var calibration = new StickCalibration();
        calibration.ApplyHardwareCalibration(new JoyCon2StickCalibration(2048, 2048, 1200, 1200, 1200, 1200));

        // A reading beyond the factory range must not widen it.
        calibration.UpdateAutoCalibration(4000, 2048, 0);
        Assert.Equal(2048 + 1200, calibration.MaxX);
        Assert.Equal((byte)255, StickReader.Normalized(new StickSample(4000, 2048), calibration).X);
    }
}
