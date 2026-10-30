using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Output;

namespace JamCon.Core.Tests;

/// <summary>Translated from upstream's SyntheticOutputCoordinatorTests.swift.</summary>
public class SyntheticOutputCoordinatorTests
{
    private static SyntheticOutputOwner Owner(string device, string control, SyntheticOutputRole role = SyntheticOutputRole.Press) =>
        new(device, control, role);

    [Fact]
    public void DuplicateReportsFromSameOwnerAreIdempotent()
    {
        var events = new List<SyntheticOutputEvent>();
        var coordinator = new SyntheticOutputCoordinator(events.Add);
        var owner = Owner("joycon", "a");
        var left = SyntheticOutput.Mouse(MouseButton.Left);

        coordinator.Set(left, true, owner);
        coordinator.Set(left, true, owner);
        coordinator.Set(left, false, owner);
        coordinator.Set(left, false, owner);

        Assert.Equal([RecordingOutputSink.Down(MouseButton.Left), RecordingOutputSink.Up(MouseButton.Left)], events);
    }

    [Fact]
    public void TwoOwnersCanHoldSameOutputWithoutPrematureRelease()
    {
        var events = new List<SyntheticOutputEvent>();
        var coordinator = new SyntheticOutputCoordinator(events.Add);
        var escape = new KeyCombo(VirtualKeys.Escape);
        var output = SyntheticOutput.Key(escape);

        coordinator.Set(output, true, Owner("left", "a"));
        coordinator.Set(output, true, Owner("right", "zr"));
        coordinator.Set(output, false, Owner("left", "a"));
        Assert.Equal([RecordingOutputSink.Down(escape)], events);

        coordinator.Set(output, false, Owner("right", "zr"));
        Assert.Equal([RecordingOutputSink.Down(escape), RecordingOutputSink.Up(escape)], events);
    }

    [Fact]
    public void DeviceDisconnectReleasesOnlyThatDevicesOutputs()
    {
        var events = new List<SyntheticOutputEvent>();
        var coordinator = new SyntheticOutputCoordinator(events.Add);
        var space = new KeyCombo(VirtualKeys.Space);
        var shared = SyntheticOutput.Key(space);

        coordinator.Set(shared, true, Owner("left", "a"));
        coordinator.Set(shared, true, Owner("right", "zr"));
        coordinator.Set(SyntheticOutput.Mouse(MouseButton.Right), true, Owner("left", "b"));

        coordinator.ReleaseAll("left");

        Assert.Equal(
            [RecordingOutputSink.Down(space), RecordingOutputSink.Down(MouseButton.Right), RecordingOutputSink.Up(MouseButton.Right)],
            events);
        Assert.Equal(1, coordinator.OwnerCount(shared));

        coordinator.ReleaseAll();
        Assert.Equal(RecordingOutputSink.Up(space), events[^1]);
    }

    [Fact]
    public void TapPulsesAlreadyHeldOutputAndPreservesHold()
    {
        var events = new List<SyntheticOutputEvent>();
        var executor = new ActionExecutor(events.Add);
        var enter = new KeyCombo(VirtualKeys.Return);
        var action = ButtonAction.Key(VirtualKeys.Return);

        executor.Execute(action, true, Owner("joycon", "a"));
        executor.Tap(action, Owner("joycon", "radialMenu.selection", SyntheticOutputRole.RadialMenu));
        executor.Execute(action, false, Owner("joycon", "a"));

        Assert.Equal(
            [RecordingOutputSink.Down(enter), RecordingOutputSink.Up(enter), RecordingOutputSink.Down(enter), RecordingOutputSink.Up(enter)],
            events);
    }

    [Fact]
    public void HoldIsReleasedWhenDeviceStateIsLost()
    {
        var events = new List<SyntheticOutputEvent>();
        var executor = new ActionExecutor(events.Add);
        var copy = new KeyCombo(VirtualKeys.C, KeyModifiers.Control);

        executor.Execute(ButtonAction.Key(VirtualKeys.C, KeyModifiers.Control), true, Owner("joycon", "a", SyntheticOutputRole.Hold));
        executor.ReleaseAll("joycon");

        Assert.Equal([RecordingOutputSink.Down(copy), RecordingOutputSink.Up(copy)], events);
    }

    [Fact]
    public void SystemShortcutUsesCoordinatedTap()
    {
        var events = new List<SyntheticOutputEvent>();
        var executor = new ActionExecutor(events.Add);
        var taskView = new KeyCombo(VirtualKeys.Tab, KeyModifiers.Win);

        executor.Execute(ButtonAction.System(SystemAction.TaskView), true, Owner("joycon", "home"));
        executor.Execute(ButtonAction.System(SystemAction.TaskView), false, Owner("joycon", "home"));

        Assert.Equal([RecordingOutputSink.Down(taskView), RecordingOutputSink.Up(taskView)], events);
    }

    [Theory]
    [InlineData(SystemAction.TaskView, VirtualKeys.Tab, KeyModifiers.Win)]
    [InlineData(SystemAction.StartMenu, (ushort)0x5B, KeyModifiers.None)]
    [InlineData(SystemAction.ShowDesktop, VirtualKeys.D, KeyModifiers.Win)]
    [InlineData(SystemAction.AppSwitcher, VirtualKeys.Tab, KeyModifiers.Control | KeyModifiers.Alt)]
    [InlineData(SystemAction.PlayPause, VirtualKeys.MediaPlayPause, KeyModifiers.None)]
    public void SystemActionsMapToWindowsShortcuts(SystemAction action, ushort key, KeyModifiers modifiers)
    {
        Assert.Equal(new KeyCombo(key, modifiers), ActionExecutor.ShortcutFor(action));
    }
}

/// <summary>Translated from upstream's DisplayGeometryTests.swift.</summary>
public class DisplayGeometryTests
{
    [Fact]
    public void PointAlreadyOnDisplayIsUnchanged()
    {
        Assert.Equal((40.0, 60.0), DisplayGeometry.ClosestPoint(40, 60, [new DisplayBounds(0, 0, 100, 100)]));
    }

    [Fact]
    public void PointInUnionGapMovesToNearestRealDisplay()
    {
        DisplayBounds[] displays = [new(0, 0, 100, 100), new(100, 50, 200, 150)];
        Assert.Equal((150.0, 50.0), DisplayGeometry.ClosestPoint(150, 25, displays));
    }

    [Fact]
    public void PointOutsideAllDisplaysUsesNearestEdge()
    {
        DisplayBounds[] displays = [new(-200, 0, -100, 100), new(0, 0, 100, 100)];
        Assert.Equal((100.0, 100.0), DisplayGeometry.ClosestPoint(140, 120, displays));
    }

    [Fact]
    public void EmptyDisplayListLeavesPointUnchanged()
    {
        Assert.Equal((12.0, 34.0), DisplayGeometry.ClosestPoint(12, 34, []));
    }
}

public class SubUnitAccumulatorTests
{
    /// <summary>Translated from upstream's testPixelScrollAccumulatorPreservesSubpixelMotion.</summary>
    [Fact]
    public void PreservesSubUnitMotion()
    {
        var accumulator = new SubUnitAccumulator();

        Assert.Equal((0, 0), accumulator.Consume(0.4, -0.4));
        Assert.Equal((0, 0), accumulator.Consume(0.4, -0.4));
        Assert.Equal((1, -1), accumulator.Consume(0.4, -0.4));
    }

    [Fact]
    public void IgnoresNonFiniteInput()
    {
        var accumulator = new SubUnitAccumulator();
        Assert.Equal((0, 0), accumulator.Consume(double.NaN, double.PositiveInfinity));
        Assert.Equal((1, 0), accumulator.Consume(1.2, 0));
    }
}

/// <summary>Translated from upstream's NumericSettingNormalizerTests.swift.</summary>
public class NumericSettingNormalizerTests
{
    [Fact]
    public void ClampsValuesToRange()
    {
        Assert.Equal(1, NumericSettingNormalizer.Normalize(-5, 1, 100, 0.5));
        Assert.Equal(100, NumericSettingNormalizer.Normalize(500, 1, 100, 0.5));
    }

    [Fact]
    public void RoundsToStepRelativeToLowerBound()
    {
        Assert.Equal(0.25, NumericSettingNormalizer.Normalize(0.26, 0.1, 5, 0.05), 0.000001);
        Assert.Equal(3, NumericSettingNormalizer.Normalize(2.76, 0.5, 10, 0.5), 0.000001);
    }

    [Fact]
    public void RejectsNonFiniteValues()
    {
        Assert.Equal(0.5, NumericSettingNormalizer.Normalize(double.PositiveInfinity, 0.5, 10, 0.5));
        Assert.Equal(1, NumericSettingNormalizer.Normalize(double.NaN, 1, 50, 1));
    }

    [Fact]
    public void InvalidStepStillClamps()
    {
        Assert.Equal(7.25, NumericSettingNormalizer.Normalize(7.25, 1, 10, 0));
    }

    /// <summary>
    /// Not in upstream's suite: guards the rounding-mode trap. 2.25 lies exactly halfway between the
    /// 2.0 and 2.5 steps. Swift rounds halves away from zero; C#'s default would pick the even step.
    /// </summary>
    [Fact]
    public void HalfwayValuesRoundAwayFromZeroLikeSwift()
    {
        Assert.Equal(2.5, NumericSettingNormalizer.Normalize(2.25, 0, 10, 0.5), 0.000001);
        Assert.Equal(1.5, NumericSettingNormalizer.Normalize(1.25, 0, 10, 0.5), 0.000001);
    }
}
