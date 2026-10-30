using JamCon.Core.Mapping;
using JamCon.Core.Output;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace JamCon.Output;

/// <summary>
/// A virtual Xbox 360 controller through the ViGEmBus driver, which games see as a real XInput pad.
/// </summary>
/// <remarks>
/// <para>
/// ViGEmBus is archived upstream but still the standard way to do this; it is installed separately
/// and is optional. Without it <see cref="IsAvailable"/> is false and Xbox controller mode is skipped.
/// </para>
/// <para>
/// The controller is plugged in on the first report and unplugged on <see cref="Release"/>, so games
/// see a pad only while JamCon is in Xbox controller mode. Called from the engine thread only.
/// </para>
/// </remarks>
public sealed class ViGEmGamepadOutput : IGamepadOutput, IDisposable
{
    private readonly Action<string> _log;
    private readonly ViGEmClient? _client;
    private IXbox360Controller? _pad;

    public ViGEmGamepadOutput(Action<string> log)
    {
        _log = log;
        try
        {
            _client = new ViGEmClient();
        }
        catch (Exception ex)
        {
            _log($"Xbox controller mode unavailable: the ViGEmBus driver was not found ({ex.GetType().Name}).");
        }
    }

    public bool IsAvailable => _client is not null;

    public void Send(GamepadReport report)
    {
        if (_client is null) return;
        try
        {
            if (_pad is null)
            {
                _pad = _client.CreateXbox360Controller();
                _pad.AutoSubmitReport = false;
                _pad.Connect();
                _log("Virtual Xbox controller plugged in.");
            }

            _pad.SetButtonsFull((ushort)((uint)report.Buttons & 0xFFFF));
            _pad.SetSliderValue(Xbox360Slider.LeftTrigger, (byte)Math.Round(report.LeftTrigger * 255));
            _pad.SetSliderValue(Xbox360Slider.RightTrigger, (byte)Math.Round(report.RightTrigger * 255));
            _pad.SetAxisValue(Xbox360Axis.LeftThumbX, Axis(report.LeftX));
            _pad.SetAxisValue(Xbox360Axis.LeftThumbY, Axis(report.LeftY));
            _pad.SetAxisValue(Xbox360Axis.RightThumbX, Axis(report.RightX));
            _pad.SetAxisValue(Xbox360Axis.RightThumbY, Axis(report.RightY));
            _pad.SubmitReport();
        }
        catch (Exception ex)
        {
            _log($"Virtual Xbox controller error: {ex.Message}");
            Unplug();
        }
    }

    public void Release() => Unplug();

    private void Unplug()
    {
        if (_pad is null) return;
        try
        {
            _pad.ResetReport();
            _pad.SubmitReport();
            _pad.Disconnect();
        }
        catch (Exception)
        {
            // Already gone; nothing left to release.
        }

        _pad = null;
        _log("Virtual Xbox controller unplugged.");
    }

    private static short Axis(double value) => (short)Math.Round(Math.Clamp(value, -1.0, 1.0) * short.MaxValue);

    public void Dispose()
    {
        Unplug();
        _client?.Dispose();
    }
}
