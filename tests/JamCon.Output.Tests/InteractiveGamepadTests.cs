using System.Runtime.InteropServices;
using JamCon.Core.Mapping;

namespace JamCon.Output.Tests;

/// <summary>
/// Plugs in a real virtual Xbox controller through ViGEmBus and reads it back through XInput, as a
/// game would. Briefly adds a controller to the system, so it runs only when
/// JAMCON_INTERACTIVE_TESTS is 1 and the driver is installed.
/// </summary>
public partial class InteractiveGamepadTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("JAMCON_INTERACTIVE_TESTS") == "1";

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftX;
        public short LeftY;
        public short RightX;
        public short RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetState(uint userIndex, out XInputState state);

    private static List<XInputGamepad> ConnectedPads()
    {
        var pads = new List<XInputGamepad>();
        for (uint i = 0; i < 4; i++)
        {
            if (XInputGetState(i, out var state) == 0) pads.Add(state.Gamepad);
        }

        return pads;
    }

    [Fact]
    public void GamesSeeTheReportAndThePadIsGoneAfterRelease()
    {
        if (!Enabled) return;
        using var pad = new ViGEmGamepadOutput(_ => { });
        Assert.True(pad.IsAvailable, "ViGEmBus is not installed; interactive runs need it.");

        var before = ConnectedPads().Count;
        pad.Send(new GamepadReport(GamepadButtons.A | GamepadButtons.RightTrigger, 0.5, 0, 0, -1));
        Thread.Sleep(500);

        var seen = ConnectedPads();
        Assert.Equal(before + 1, seen.Count);
        var ours = Assert.Single(seen, p => p.Buttons == 0x1000);
        Assert.Equal(255, ours.RightTrigger);
        Assert.InRange(ours.LeftX, 16000, 16800);
        Assert.Equal(-32767, ours.RightY);

        pad.Release();
        Thread.Sleep(500);
        Assert.Equal(before, ConnectedPads().Count);
    }
}
