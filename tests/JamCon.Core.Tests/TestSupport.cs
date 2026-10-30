using JamCon.Core.Engine;
using JamCon.Core.Mapping;
using JamCon.Core.Output;
using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>Records everything the engine emits, in order.</summary>
internal sealed class RecordingOutputSink : IOutputSink
{
    public List<SyntheticOutputEvent> Events { get; } = [];
    public List<(double Dx, double Dy)> Moves { get; } = [];
    public List<(double Dx, double Dy)> Scrolls { get; } = [];

    public void Post(SyntheticOutputEvent outputEvent) => Events.Add(outputEvent);
    public void MoveCursor(double dx, double dy) => Moves.Add((dx, dy));
    public void Scroll(double dx, double dy) => Scrolls.Add((dx, dy));

    public static SyntheticOutputEvent Down(MouseButton button) => new(SyntheticOutput.Mouse(button), true);
    public static SyntheticOutputEvent Up(MouseButton button) => new(SyntheticOutput.Mouse(button), false);
    public static SyntheticOutputEvent Down(KeyCombo combo) => new(SyntheticOutput.Key(combo), true);
    public static SyntheticOutputEvent Up(KeyCombo combo) => new(SyntheticOutput.Key(combo), false);
}

/// <summary>Holds fire only when a test says so, like upstream's ManualHoldScheduler.</summary>
internal sealed class ManualHoldScheduler : IHoldScheduler
{
    private readonly List<Pending> _pending = [];

    public IHoldTimer Schedule(TimeSpan delay, Action callback)
    {
        var timer = new Pending(delay, callback);
        _pending.Add(timer);
        return timer;
    }

    public void FireAll()
    {
        var work = _pending.ToArray();
        _pending.Clear();
        foreach (var timer in work.Where(t => !t.Cancelled)) timer.Callback();
    }

    /// <summary>Fires timers due by <paramref name="now"/>, for replaying captures on their own clock.</summary>
    public void Advance(double now)
    {
        foreach (var timer in _pending.Where(t => !t.Cancelled && t.DueAt is { } due && due <= now).ToArray())
        {
            _pending.Remove(timer);
            timer.Callback();
        }
    }

    /// <summary>Stamps newly scheduled timers with a due time on the replay clock.</summary>
    public void StampPending(double now)
    {
        foreach (var timer in _pending.Where(t => t.DueAt is null)) timer.DueAt = now + timer.Delay.TotalSeconds;
    }

    private sealed class Pending(TimeSpan delay, Action callback) : IHoldTimer
    {
        public TimeSpan Delay { get; } = delay;
        public Action Callback { get; } = callback;
        public bool Cancelled { get; private set; }
        public double? DueAt { get; set; }
        public void Cancel() => Cancelled = true;
    }
}

/// <summary>Builds common input report 0x05 payloads with chosen field values.</summary>
internal static class Frames
{
    public static byte[] Build(
        uint buttons = 0,
        ushort stickX = 2048,
        ushort stickY = 2048,
        short gyroX = 0,
        short gyroY = 0,
        short gyroZ = 0,
        JoyCon2Handedness handedness = JoyCon2Handedness.Right)
    {
        var frame = new byte[JoyCon2ReportDecoder.MinimumLength];
        BitConverter.TryWriteBytes(frame.AsSpan(0x04), buttons);
        PackStick(frame, handedness == JoyCon2Handedness.Right ? 0x0D : 0x0A, stickX, stickY);

        // A non-zero accelerometer keeps the frame from reading as the zero-filled startup report.
        BitConverter.TryWriteBytes(frame.AsSpan(0x34), (short)4096);
        BitConverter.TryWriteBytes(frame.AsSpan(0x36), gyroX);
        BitConverter.TryWriteBytes(frame.AsSpan(0x38), gyroY);
        BitConverter.TryWriteBytes(frame.AsSpan(0x3A), gyroZ);
        return frame;
    }

    public static JoyCon2Report Report(
        uint buttons = 0,
        ushort stickX = 2048,
        ushort stickY = 2048,
        short gyroX = 0,
        short gyroY = 0,
        short gyroZ = 0,
        JoyCon2Handedness handedness = JoyCon2Handedness.Right)
    {
        Assert.True(JoyCon2ReportDecoder.TryDecode(Build(buttons, stickX, stickY, gyroX, gyroY, gyroZ, handedness), out var report));
        return report;
    }

    public static uint Bit(JoyCon2Button button, JoyCon2Handedness handedness = JoyCon2Handedness.Right) =>
        1u << JoyCon2ButtonLayout.BitFor(button, handedness)!.Value;

    public static void PackStick(byte[] buffer, int offset, int x, int y)
    {
        buffer[offset] = (byte)(x & 0xFF);
        buffer[offset + 1] = (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4));
        buffer[offset + 2] = (byte)((y >> 4) & 0xFF);
    }
}
