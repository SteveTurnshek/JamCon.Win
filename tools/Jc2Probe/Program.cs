using System.Diagnostics;
using JamCon.Bluetooth;
using JamCon.Core.Diagnostics;
using JamCon.Core.Gyro;
using JamCon.Core.Protocol;
using Jc2Probe;

// Phase 1 BLE transport spike. Console only: no mapping, no mouse output, no UI.
// Answers the open questions in docs/Windows-Port.md by measurement, not by reasoning.

var options = ProbeOptions.Parse(args);
if (options is null) return 1;

Console.WriteLine($"Jc2Probe — Joy-Con 2 BLE transport spike (mode: {(options.Dual ? "dual" : "single")})");
Console.WriteLine();

var radio = await BluetoothRadioState.CheckAsync();
Console.WriteLine($"Bluetooth: {radio.Availability} — {radio.Detail}");
if (!radio.IsReady)
{
    Console.WriteLine();
    Console.WriteLine("Cannot scan without a usable Bluetooth radio. Nothing was measured.");
    return 4;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine();
    Console.WriteLine("Stopping...");
    cts.Cancel();
};

List<JoyCon2Discovery> discoveries = [];

if (!options.UsePaired)
{
    Console.WriteLine();
    Console.WriteLine("  >>> PRESS AND HOLD THE SYNC BUTTON NOW <<<");
    Console.WriteLine("  The pairing window is short, so press SYNC while this scan is counting down,");
    Console.WriteLine("  not before you started the command.");
    Console.WriteLine();
    Console.WriteLine($"Scanning up to {options.ScanSeconds}s for Nintendo manufacturer data (vendor 0x057E)...");
    Console.WriteLine();

    discoveries = await ScanAsync(options, cts.Token);
}

if (discoveries.Count == 0)
{
    Console.WriteLine();
    Console.WriteLine("No Joy-Con 2 discovered by scanning. Checking devices already paired to Windows...");
    Console.WriteLine();

    var paired = await PairedJoyCon2Finder.FindAsync(m => Console.WriteLine($"  {m}"));
    foreach (var candidate in paired)
    {
        Console.WriteLine(
            $"  paired: {candidate.Name}  addr={candidate.BluetoothAddress:X12}  " +
            $"jc2-service={(candidate.ServiceConfirmed ? "confirmed" : "unconfirmed")}  " +
            $"connected={candidate.IsConnected}");
    }

    var usable = paired.Where(p => p.ServiceConfirmed).ToArray();
    if (usable.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Using {usable.Length} already-paired Joy-Con 2 controller(s) instead of scanning.");
        discoveries = usable
            .Select(p => new JoyCon2Discovery(
                p.BluetoothAddress,
                new JoyCon2Identity(
                    JoyCon2Ids.NintendoVendorId,
                    p.Handedness == JoyCon2Handedness.Left
                        ? JoyCon2Ids.LeftJoyCon2ProductId
                        : JoyCon2Ids.RightJoyCon2ProductId,
                    p.Handedness,
                    PayloadIncludedCompanyId: false),
                SignalStrengthDbm: 0,
                RawManufacturerData: []))
            .ToList();
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine("Nothing usable found. Things to check, in order:");
        Console.WriteLine("  1. Press SYNC *during* the scan countdown, not before the command starts.");
        Console.WriteLine("  2. If a Switch 2 is powered on nearby, the Joy-Con reconnects to it instead.");
        Console.WriteLine("     Turn the console off or move out of its range.");
        Console.WriteLine("  3. Try a longer window:  --scan-seconds 60");
        Console.WriteLine("  4. If a Nintendo advertisement was listed above but did not parse, paste the");
        Console.WriteLine("     raw bytes — the manufacturer-data offsets then need correcting.");
        return 2;
    }
}

var targets = SelectTargets(discoveries, options);
Console.WriteLine();
Console.WriteLine($"Connecting to {targets.Count} controller(s).");
Console.WriteLine();

Directory.CreateDirectory(options.OutputDirectory);
var monitors = new List<DeviceMonitor>();

foreach (var target in targets)
{
    var monitor = new DeviceMonitor(target, options);
    monitors.Add(monitor);
    if (!await monitor.StartAsync(cts.Token))
    {
        Console.WriteLine($"[{monitor.Label}] initialization failed. See the log above.");
    }
}

if (monitors.All(m => !m.IsStreaming))
{
    Console.WriteLine();
    Console.WriteLine("No controller reached the streaming state. Nothing to measure.");
    foreach (var monitor in monitors) monitor.Dispose();
    return 3;
}

await ReportLoopAsync(monitors, options, cts.Token);

Console.WriteLine();
Console.WriteLine("=== Summary ===");
foreach (var monitor in monitors) monitor.PrintSummary();
foreach (var monitor in monitors) monitor.Dispose();

Console.WriteLine();
Console.WriteLine("Record these findings in docs/Windows-Port.md, and commit the .bin as a Phase 2 fixture.");
return 0;

static async Task<List<JoyCon2Discovery>> ScanAsync(ProbeOptions options, CancellationToken ct)
{
    using var scanner = new JoyCon2Scanner();
    var found = new List<JoyCon2Discovery>();

    scanner.Discovered += discovery =>
    {
        lock (found) found.Add(discovery);
        Console.WriteLine(
            $"  found {discovery.DisplayName}  addr={discovery.AddressString}  " +
            $"rssi={discovery.SignalStrengthDbm} dBm  pid=0x{discovery.Identity.ProductId:X4}");
        Console.WriteLine(
            $"    manufacturer data: {Convert.ToHexString(discovery.RawManufacturerData)}");
        Console.WriteLine(
            $"    payload included company id: {discovery.Identity.PayloadIncludedCompanyId} " +
            "(false = WinRT offsets, as expected on Windows)");
    };

    scanner.UnrecognizedNintendoAdvertisement += (address, payload) =>
        Console.WriteLine(
            $"  [!] Nintendo advertisement that did not parse as Joy-Con 2: " +
            $"addr=0x{address:X12} data={Convert.ToHexString(payload)}");

    try
    {
        scanner.Start();
    }
    catch (BluetoothUnavailableException ex)
    {
        Console.WriteLine($"  [!] {ex.Message}");
        return found;
    }

    try
    {
        var deadline = Stopwatch.StartNew();
        var wanted = options.Dual ? 2 : 1;
        var lastSecondShown = -1;

        while (deadline.Elapsed.TotalSeconds < options.ScanSeconds && !ct.IsCancellationRequested)
        {
            lock (found)
            {
                if (found.Count >= wanted) break;
            }

            // A live counter proves the radio is actually receiving. Zero advertisements after
            // several seconds means scanning is broken, which is a different problem from the
            // controller not being discoverable.
            var remaining = (int)Math.Ceiling(options.ScanSeconds - deadline.Elapsed.TotalSeconds);
            if (remaining != lastSecondShown)
            {
                lastSecondShown = remaining;
                Console.Write(
                    $"\r  {remaining,3}s left | advertisements: {scanner.TotalAdvertisements,5} " +
                    $"| distinct devices: {scanner.DistinctDevices,3} | Joy-Con 2 found: {found.Count}   ");
            }

            await Task.Delay(150, CancellationToken.None);
        }

        Console.WriteLine();
    }
    finally
    {
        scanner.Stop();
    }

    Console.WriteLine();
    Console.WriteLine(
        $"Scan finished: {scanner.TotalAdvertisements} advertisements from " +
        $"{scanner.DistinctDevices} distinct devices.");

    if (scanner.TotalAdvertisements == 0)
    {
        Console.WriteLine(
            "  [!] Zero advertisements from any device. The radio is on but not receiving — " +
            "that is a Windows Bluetooth problem, not a controller problem.");
    }

    foreach (var name in scanner.InterestingNames)
    {
        Console.WriteLine($"  [*] Nearby device with a suggestive name: {name}");
    }

    lock (found) return found.ToList();
}

static List<JoyCon2Discovery> SelectTargets(List<JoyCon2Discovery> discoveries, ProbeOptions options)
{
    if (options.Dual) return discoveries.Take(2).ToList();

    // Single mode prefers the right Joy-Con: it is the variant upstream validated end to end,
    // so a divergence is attributable to Windows rather than to an untested controller.
    var right = discoveries.FirstOrDefault(d => d.Identity.Handedness == JoyCon2Handedness.Right);
    return [right ?? discoveries[0]];
}

static async Task ReportLoopAsync(List<DeviceMonitor> monitors, ProbeOptions options, CancellationToken ct)
{
    Console.WriteLine();
    Console.WriteLine($"Streaming. Reporting once per second for up to {options.DurationSeconds}s. Ctrl+C to stop early.");
    Console.WriteLine();

    var elapsed = Stopwatch.StartNew();
    while (elapsed.Elapsed.TotalSeconds < options.DurationSeconds && !ct.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(1000, ct);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        foreach (var monitor in monitors) monitor.PrintTick();
    }
}

/// <summary>Parsed command line.</summary>
internal sealed record ProbeOptions(
    bool Dual,
    int ScanSeconds,
    int DurationSeconds,
    string OutputDirectory,
    bool UsePaired)
{
    public static ProbeOptions? Parse(string[] args)
    {
        var dual = false;
        var scanSeconds = 30;
        var durationSeconds = 300;
        var usePaired = false;
        var output = Path.Combine(Environment.CurrentDirectory, "captures");

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dual":
                    dual = true;
                    break;
                case "--use-paired":
                    usePaired = true;
                    break;
                case "--scan-seconds" when i + 1 < args.Length:
                    scanSeconds = int.Parse(args[++i]);
                    break;
                case "--seconds" when i + 1 < args.Length:
                    durationSeconds = int.Parse(args[++i]);
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "-h":
                case "--help":
                    PrintUsage();
                    return null;
                default:
                    Console.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage();
                    return null;
            }
        }

        return new ProbeOptions(dual, scanSeconds, durationSeconds, output, usePaired);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: Jc2Probe [--dual] [--use-paired] [--scan-seconds N] [--seconds N] [--out DIR]");
        Console.WriteLine();
        Console.WriteLine("  --dual           Connect to two Joy-Con 2s and measure each one's cadence.");
        Console.WriteLine("                   This is the open question with no prior evidence anywhere.");
        Console.WriteLine("  --use-paired     Skip scanning and use a controller already paired to Windows.");
        Console.WriteLine("                   Use this when the SYNC pairing window keeps closing too fast.");
        Console.WriteLine("  --scan-seconds   How long to scan for advertisements (default 30).");
        Console.WriteLine("  --seconds        How long to stream before summarizing (default 300).");
        Console.WriteLine("                   The exit criterion is 60+ reports/sec sustained for 5 minutes.");
        Console.WriteLine("  --out            Directory for .bin and .jsonl captures (default ./captures).");
    }
}

/// <summary>Owns one session and its measurements.</summary>
internal sealed class DeviceMonitor(JoyCon2Discovery discovery, ProbeOptions options) : IDisposable
{
    private readonly CadenceEstimator _cadence = new();
    private readonly Stopwatch _uptime = new();
    private JoyCon2Session? _session;
    private FrameRecorder? _recorder;

    private long _frames;
    private long _framesAtLastTick;
    private long _undecodableFrames;
    private long _zeroFilledImuFrames;
    private double _lastTimestamp;
    private double _worstGapSeconds;
    private int _disconnectCount;
    private JoyCon2Report? _latest;

    public string Label { get; } = $"{discovery.DisplayName} {discovery.AddressString}";
    public bool IsStreaming { get; private set; }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        var safeName = discovery.AddressString.Replace(':', '-');
        var basePath = Path.Combine(
            options.OutputDirectory,
            $"jc2-{discovery.Identity.Handedness.ToString().ToLowerInvariant()}-{safeName}-{DateTime.Now:yyyyMMdd-HHmmss}");
        _recorder = new FrameRecorder(basePath, Label);

        _session = new JoyCon2Session(discovery.BluetoothAddress, message => Console.WriteLine($"[{Label}] {message}"));
        _session.FrameReceived += OnFrame;
        _session.ConnectionChanged += connected =>
        {
            if (!connected)
            {
                Interlocked.Increment(ref _disconnectCount);
                Console.WriteLine($"[{Label}] disconnected.");
            }
        };

        var ok = await _session.ConnectAsync(discovery.Identity.Handedness, ct);
        if (ok)
        {
            IsStreaming = true;
            _uptime.Restart();
            Console.WriteLine($"[{Label}] streaming. Capture: {_recorder.BinaryPath}");
        }

        return ok;
    }

    private void OnFrame(Jc2Frame frame)
    {
        Interlocked.Increment(ref _frames);
        _recorder?.Record(frame);
        _cadence.Record(frame.TimestampSeconds);

        if (_lastTimestamp > 0)
        {
            var gap = frame.TimestampSeconds - _lastTimestamp;
            if (gap > _worstGapSeconds) _worstGapSeconds = gap;
        }

        _lastTimestamp = frame.TimestampSeconds;

        if (frame.Report is { } report)
        {
            _latest = report;
            if (report.Imu.IsZeroFilled) Interlocked.Increment(ref _zeroFilledImuFrames);
        }
        else
        {
            Interlocked.Increment(ref _undecodableFrames);
        }
    }

    public void PrintTick()
    {
        var total = Interlocked.Read(ref _frames);
        var lastSecond = total - _framesAtLastTick;
        _framesAtLastTick = total;

        var line = $"[{Label}] {lastSecond,3} fps (median {_cadence.EstimatedRateHz,6:F1} Hz)  frames={total}";

        if (_latest is { } report)
        {
            var gyro = report.Imu;
            line += $"  gyro=({Scale(gyro.GyroX),7:F1},{Scale(gyro.GyroY),7:F1},{Scale(gyro.GyroZ),7:F1}) deg/s" +
                    $"  stickR=({report.RightStick.X,4},{report.RightStick.Y,4})" +
                    $"  buttons=0x{report.Buttons:X8}";

            var pressed = report.PressedBitIndices().ToArray();
            if (pressed.Length > 0) line += $" bits[{string.Join(',', pressed)}]";
        }

        Console.WriteLine(line);
    }

    private static double Scale(short raw) => raw * GyroConstants.DegreesPerSecondPerCount;

    public void PrintSummary()
    {
        var total = Interlocked.Read(ref _frames);
        var seconds = _uptime.Elapsed.TotalSeconds;
        var average = seconds > 0 ? total / seconds : 0;

        Console.WriteLine();
        Console.WriteLine($"[{Label}]");
        Console.WriteLine($"  connect path        : {_session?.ConnectPath}  <-- open question #1");
        Console.WriteLine($"  report rate accepted: {_session?.NegotiatedReportRate?.ToString() ?? "controller default"}");
        Console.WriteLine($"  frames              : {total} over {seconds:F1}s");
        Console.WriteLine($"  average rate        : {average:F1} reports/sec  <-- open question #2 (exit criterion: >=60)");
        Console.WriteLine($"  median rate         : {_cadence.EstimatedRateHz:F1} Hz");
        Console.WriteLine($"  worst gap           : {_worstGapSeconds * 1000:F1} ms");
        Console.WriteLine($"  undecodable frames  : {Interlocked.Read(ref _undecodableFrames)}");
        Console.WriteLine($"  zero-filled IMU     : {Interlocked.Read(ref _zeroFilledImuFrames)} (one at startup is expected)");
        Console.WriteLine($"  disconnects         : {_disconnectCount}  <-- open question #4");
        Console.WriteLine($"  stick calibration   : {(_session?.Calibration is { } c ? $"center=({c.CenterX},{c.CenterY})" : "not read")}");
        Console.WriteLine($"  capture             : {_recorder?.BinaryPath}");
        Console.WriteLine($"  frames recorded     : {_recorder?.FrameCount}");
    }

    public void Dispose()
    {
        if (_session is not null)
        {
            _session.FrameReceived -= OnFrame;
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _recorder?.Dispose();
    }
}
