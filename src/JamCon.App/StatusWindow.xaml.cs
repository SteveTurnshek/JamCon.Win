using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using JamCon.App.Services;
using JamCon.App.Tray;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
using JamCon.Core.Gyro;

namespace JamCon.App;

/// <summary>
/// Live view of the link and its log. Closing it only hides it; the app keeps running in the tray.
/// </summary>
public partial class StatusWindow : Window
{
    private const int MaxLogLines = 500;

    /// <summary>
    /// Display names for the button bits verified on hardware (docs/Windows-Port.md). Display only:
    /// real mapping belongs in JamCon.Core and arrives with the Phase 2 port.
    /// </summary>
    private static readonly (int Bit, string Name)[] VerifiedButtons =
    [
        (0, "Y"), (1, "X"), (2, "B"), (3, "A"), (4, "SR"), (5, "SL"), (6, "R"), (7, "ZR"),
        (9, "Plus"), (10, "Stick"), (14, "C"),
    ];

    private readonly JoyCon2Link _link;
    private readonly FileLog _log;
    private readonly SettingsStore _input;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, TextBlock> _values = new();
    private bool _exiting;

    internal StatusWindow(JoyCon2Link link, FileLog log, SettingsStore input)
    {
        InitializeComponent();
        _link = link;
        _input = input;
        _log = log;

        foreach (var key in (string[])
                 ["Output", "Controller", "Report rate", "Frames", "Connected for", "Reconnects",
                  "Last drop", "Last reconnect took", "Gyro (°/s)", "Stick", "Buttons"])
        {
            AddFact(key);
        }

        foreach (var line in _log.Recent()) LogList.Items.Add(line);
        _log.LineWritten += OnLogLine;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Update();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Update();
                ScrollLogToEnd();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private void AddFact(string key)
    {
        var row = Facts.RowDefinitions.Count;
        Facts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock { Text = key, Margin = new Thickness(0, 1, 16, 1), Opacity = 0.75 };
        var value = new TextBlock { Margin = new Thickness(0, 1, 0, 1), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(label, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        Facts.Children.Add(label);
        Facts.Children.Add(value);
        _values[key] = value;
    }

    private void Update()
    {
        var s = _link.Status;
        Headline.Text = TrayController.Summarize(s);
        Detail.Text = s.Detail;
        _values["Output"].Text = TrayController.OutputHint(s, _input.Snapshot()) ?? "Waiting for a controller";

        _values["Controller"].Text = s.Controller is { } c
            ? $"{c.DisplayName}  {JoyCon2Link.Format(c.Address)}"
            : "none remembered";
        _values["Report rate"].Text = s.State == JoyCon2LinkState.Streaming ? $"{s.RateHz:F1} Hz" : "—";
        _values["Frames"].Text = s.Frames.ToString("N0");
        _values["Connected for"].Text = s.StreamingSince is { } since ? Format(DateTimeOffset.Now - since) : "—";
        _values["Reconnects"].Text = s.Reconnects.ToString();
        _values["Last drop"].Text = s.LastDropReason ?? "—";
        _values["Last reconnect took"].Text = s.LastReconnectDuration is { } took ? Format(took) : "—";

        if (s.LastReport is { } r && s.State == JoyCon2LinkState.Streaming)
        {
            var k = GyroConstants.DegreesPerSecondPerCount;
            _values["Gyro (°/s)"].Text = $"X {r.Imu.GyroX * k,7:F1}   Y {r.Imu.GyroY * k,7:F1}   Z {r.Imu.GyroZ * k,7:F1}";
            _values["Stick"].Text = $"{r.RightStick.X}, {r.RightStick.Y}";
            var pressed = VerifiedButtons.Where(b => (r.Buttons & (1u << b.Bit)) != 0).Select(b => b.Name).ToArray();
            _values["Buttons"].Text = pressed.Length == 0 ? "none" : string.Join(" ", pressed);
        }
        else
        {
            _values["Gyro (°/s)"].Text = "—";
            _values["Stick"].Text = "—";
            _values["Buttons"].Text = "—";
        }
    }

    private static string Format(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
        : span.TotalMinutes >= 1
            ? $"{span.Minutes}m {span.Seconds}s"
            : $"{span.TotalSeconds:F1} s";

    private void OnLogLine(string line) => Dispatcher.BeginInvoke(() =>
    {
        LogList.Items.Add(line);
        while (LogList.Items.Count > MaxLogLines) LogList.Items.RemoveAt(0);
        if (IsVisible) ScrollLogToEnd();
    });

    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Actually closes the window, for application exit.</summary>
    internal void CloseForExit()
    {
        _exiting = true;
        _log.LineWritten -= OnLogLine;
        _timer.Stop();
        Close();
    }
}
