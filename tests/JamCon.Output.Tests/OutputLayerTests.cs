using JamCon.Core.Mapping;
using static JamCon.Output.NativeMethods;

namespace JamCon.Output.Tests;

public unsafe class NativeLayoutTests
{
    /// <summary>
    /// SendInput rejects every event when cbSize is wrong, with no other symptom than nothing
    /// happening. Win32 defines INPUT as 40 bytes on 64-bit and 28 on 32-bit.
    /// </summary>
    [Fact]
    public void InputStructMatchesWin32Size()
    {
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, sizeof(Input));
    }

    [Fact]
    public void UnionStartsAtPointerAlignment()
    {
        var input = new Input();
        var offset = (byte*)&input.Union - (byte*)&input;
        Assert.Equal(IntPtr.Size == 8 ? 8 : 4, offset);
    }
}

public class AbsoluteCoordinateTests
{
    /// <summary>
    /// Every pixel must survive the round trip through Windows' 0–65535 range exactly, whether
    /// Windows truncates or rounds on the way back. The naive pixel × 65535 / width fails this.
    /// </summary>
    [Theory]
    [InlineData(0, 1280)]
    [InlineData(0, 1920)]
    [InlineData(-1920, 3840)]
    [InlineData(0, 2560)]
    [InlineData(0, 3440)]
    [InlineData(-2560, 7680)]
    [InlineData(0, 1080)]
    [InlineData(-440, 2160)]
    public void EveryPixelRoundTripsExactly(int origin, int extent)
    {
        for (var pixel = origin; pixel < origin + extent; pixel++)
        {
            var normalized = AbsoluteCoordinates.Normalize(pixel, origin, extent);
            Assert.InRange(normalized, 0, 65535);
            Assert.Equal(pixel, AbsoluteCoordinates.ToPixelTruncating(normalized, origin, extent));
            Assert.Equal(pixel, AbsoluteCoordinates.ToPixelRounding(normalized, origin, extent));
        }
    }

    [Fact]
    public void NaiveFormulaWouldHaveBeenWrong()
    {
        // Documents why the rounding-up formula exists: the common pixel * 65535 / width lands short.
        const int width = 1920;
        var misses = Enumerable.Range(0, width)
            .Count(p => AbsoluteCoordinates.ToPixelTruncating(p * 65535 / width, 0, width) != p);
        Assert.True(misses > 0);
    }

    [Fact]
    public void OutOfRangePixelsClamp()
    {
        Assert.Equal(0, AbsoluteCoordinates.Normalize(-50, 0, 1920));
        Assert.Equal(1919, AbsoluteCoordinates.ToPixelTruncating(AbsoluteCoordinates.Normalize(5000, 0, 1920), 0, 1920));
    }
}

public class KeyboardInputTests
{
    [Fact]
    public void ModifiersGoDownFirstAndComeUpLast()
    {
        var combo = new KeyCombo(VirtualKeys.C, KeyModifiers.Control | KeyModifiers.Shift);

        var down = KeyboardInputs.For(combo, isPressed: true);
        var up = KeyboardInputs.For(combo, isPressed: false);

        Assert.Equal(3, down.Length);
        Assert.All(down, i => Assert.Equal(InputKeyboard, i.Type));
        Assert.All(down, i => Assert.Equal(0u, i.Union.Keyboard.Flags & KeyEventKeyUp));
        Assert.All(up, i => Assert.Equal(KeyEventKeyUp, i.Union.Keyboard.Flags & KeyEventKeyUp));

        var downScans = down.Select(i => i.Union.Keyboard.ScanCode).ToArray();
        var upScans = up.Select(i => i.Union.Keyboard.ScanCode).ToArray();
        Assert.Equal(Enumerable.Reverse(downScans), upScans);
    }

    /// <summary>Ordinary keys use scan codes, which games read; this is the plan's requirement.</summary>
    [Fact]
    public void OrdinaryKeysAreInjectedByScanCode()
    {
        var escape = KeyboardInputs.Key(VirtualKeys.Escape, isPressed: true);
        Assert.Equal(KeyEventScanCode, escape.Flags & KeyEventScanCode);
        Assert.Equal(0x01, escape.ScanCode);
        Assert.Equal(0u, escape.Flags & KeyEventExtendedKey);
    }

    /// <summary>Arrow keys and the Windows key are E0-prefixed; without the flag they become numpad keys.</summary>
    [Theory]
    [InlineData(VirtualKeys.Down, 0x50)]
    [InlineData(VirtualKeys.Up, 0x48)]
    [InlineData((ushort)0x5B, 0x5B)]
    public void ExtendedKeysCarryTheExtendedFlag(ushort vk, ushort scan)
    {
        var input = KeyboardInputs.Key(vk, isPressed: true);
        Assert.Equal(KeyEventScanCode, input.Flags & KeyEventScanCode);
        Assert.Equal(KeyEventExtendedKey, input.Flags & KeyEventExtendedKey);
        Assert.Equal(scan, input.ScanCode);
    }

    [Theory]
    [InlineData(VirtualKeys.BrowserBack)]
    [InlineData(VirtualKeys.BrowserForward)]
    [InlineData(VirtualKeys.MediaPlayPause)]
    public void AppCommandKeysAreInjectedByVirtualKey(ushort vk)
    {
        var input = KeyboardInputs.Key(vk, isPressed: true);
        Assert.Equal(vk, input.VirtualKey);
        Assert.Equal(0u, input.Flags & KeyEventScanCode);
        Assert.Equal(KeyEventExtendedKey, input.Flags & KeyEventExtendedKey);
    }
}

public class MonitorTests
{
    [Fact]
    public void AtLeastOneMonitorWithValidBounds()
    {
        var monitors = SendInputOutputSink.Monitors();
        Assert.NotEmpty(monitors);
        Assert.All(monitors, m => Assert.False(m.IsEmpty));
    }
}
