using System.Diagnostics;
using JamCon.Core.Output;

namespace JamCon.Output.Tests;

public class PacingTests
{
    private sealed class CountingSink : IOutputSink
    {
        private readonly object _gate = new();
        public int Moves { get; private set; }
        public double TotalX { get; private set; }
        public List<SyntheticOutputEvent> Events { get; } = [];
        public double XAtFirstEvent { get; private set; } = double.NaN;

        public void Post(SyntheticOutputEvent outputEvent)
        {
            lock (_gate)
            {
                if (Events.Count == 0) XAtFirstEvent = TotalX;
                Events.Add(outputEvent);
            }
        }

        public void MoveCursor(double dx, double dy)
        {
            lock (_gate)
            {
                Moves++;
                TotalX += dx;
            }
        }

        public void Scroll(double dx, double dy)
        {
        }

        public void Look(double dx, double dy)
        {
        }
    }

    [Fact]
    public void HighResolutionTimerIsAvailable()
    {
        using var waiter = new PreciseWaiter();
        Assert.True(waiter.IsHighResolution);
    }

    [Fact]
    public void PreciseWaitsAreFarShorterThanTheDefaultTimerTick()
    {
        using var waiter = new PreciseWaiter();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < 50; i++)
        {
            waiter.WaitUntil(Stopwatch.GetTimestamp() + Stopwatch.Frequency / 500); // 2 ms
        }

        // 50 × 2 ms = 100 ms. With the default 15.6 ms tick it would take about 780 ms.
        Assert.InRange(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 95, 400);
    }

    /// <summary>
    /// 66 Hz input comes out at roughly 240 Hz, with the total motion unchanged. Timing-based, so the
    /// bounds are loose; the property under test is "several updates per report", not exact rates.
    /// </summary>
    [Fact]
    public void SixtySixHertzInputIsEmittedAtSeveralTimesTheRate()
    {
        var inner = new CountingSink();
        var paced = new PacedOutputSink(inner);
        var start = Stopwatch.GetTimestamp();
        var reports = 0;
        while (Stopwatch.GetElapsedTime(start).TotalSeconds < 1.0)
        {
            paced.MoveCursor(3, 0);
            reports++;
            Thread.Sleep(15);
        }

        paced.Dispose();

        Assert.True(inner.Moves > reports * 2, $"{inner.Moves} moves for {reports} reports");
        Assert.Equal(reports * 3.0, inner.TotalX, 1e-6);
    }

    [Fact]
    public void ClicksFlushPendingMotionFirst()
    {
        var inner = new CountingSink();
        using var paced = new PacedOutputSink(inner, spreadSeconds: 0.05);

        paced.MoveCursor(40, 0);
        paced.Post(new SyntheticOutputEvent(SyntheticOutput.Mouse(Core.Mapping.MouseButton.Left), true));

        // All 40 px must have been delivered before the click, not spread over the next 50 ms.
        Assert.Equal(40, inner.XAtFirstEvent, 1e-9);
    }
}
