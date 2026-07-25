using System.Globalization;
using System.Text;
using JamCon.Bluetooth;

namespace Jc2Probe;

/// <summary>
/// Writes captured frames in two forms, per the Phase 1 decision:
/// a byte-exact <c>.bin</c> that becomes the authoritative golden-vector fixture, and a
/// <c>.jsonl</c> sidecar that is diffable and readable when something looks wrong.
/// </summary>
/// <remarks>
/// The binary format is deliberately trivial so a fixture reader cannot get it wrong:
/// a 16-byte header, then per frame a 4-byte little-endian length, an 8-byte little-endian
/// timestamp in microseconds, then the raw payload bytes exactly as the radio delivered them.
/// </remarks>
public sealed class FrameRecorder : IDisposable
{
    private const uint Magic = 0x324A4331; // "1CJ2"
    private const uint FormatVersion = 1;

    private readonly FileStream _binary;
    private readonly StreamWriter _jsonl;
    private readonly object _gate = new();

    public string BinaryPath { get; }
    public string JsonlPath { get; }
    public int FrameCount { get; private set; }

    public FrameRecorder(string basePath, string deviceLabel)
    {
        BinaryPath = basePath + ".bin";
        JsonlPath = basePath + ".jsonl";

        _binary = new FileStream(BinaryPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _jsonl = new StreamWriter(
            new FileStream(JsonlPath, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));

        WriteHeader();
        WriteJsonlMetadata(deviceLabel);
    }

    private void WriteHeader()
    {
        Span<byte> header = stackalloc byte[16];
        BitConverter.TryWriteBytes(header[..4], Magic);
        BitConverter.TryWriteBytes(header[4..8], FormatVersion);
        // Remaining eight bytes are reserved and zero, so the header stays a fixed size if the
        // format ever needs a flag field.
        _binary.Write(header);
    }

    private void WriteJsonlMetadata(string deviceLabel) =>
        _jsonl.WriteLine(
            $"{{\"type\":\"metadata\",\"device\":{Quote(deviceLabel)}," +
            $"\"capturedUtc\":{Quote(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))}," +
            $"\"binaryFormatVersion\":{FormatVersion}}}");

    public void Record(Jc2Frame frame)
    {
        lock (_gate)
        {
            Span<byte> prefix = stackalloc byte[12];
            BitConverter.TryWriteBytes(prefix[..4], frame.Payload.Length);
            BitConverter.TryWriteBytes(prefix[4..12], (long)(frame.TimestampSeconds * 1_000_000.0));
            _binary.Write(prefix);
            _binary.Write(frame.Payload);

            _jsonl.WriteLine(BuildJsonLine(frame));
            FrameCount++;
        }
    }

    private static string BuildJsonLine(Jc2Frame frame)
    {
        var builder = new StringBuilder(256);
        builder.Append("{\"type\":\"frame\",\"t\":")
            .Append(frame.TimestampSeconds.ToString("F6", CultureInfo.InvariantCulture))
            .Append(",\"len\":").Append(frame.Payload.Length)
            .Append(",\"raw\":\"").Append(Convert.ToHexString(frame.Payload)).Append('"');

        if (frame.Report is { } report)
        {
            builder.Append(",\"buttons\":").Append(report.Buttons)
                .Append(",\"leftStick\":{\"x\":").Append(report.LeftStick.X)
                .Append(",\"y\":").Append(report.LeftStick.Y).Append('}')
                .Append(",\"rightStick\":{\"x\":").Append(report.RightStick.X)
                .Append(",\"y\":").Append(report.RightStick.Y).Append('}')
                .Append(",\"accel\":{\"x\":").Append(report.Imu.AccelX)
                .Append(",\"y\":").Append(report.Imu.AccelY)
                .Append(",\"z\":").Append(report.Imu.AccelZ).Append('}')
                .Append(",\"gyro\":{\"x\":").Append(report.Imu.GyroX)
                .Append(",\"y\":").Append(report.Imu.GyroY)
                .Append(",\"z\":").Append(report.Imu.GyroZ).Append('}');
        }
        else
        {
            builder.Append(",\"decoded\":false");
        }

        return builder.Append('}').ToString();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public void Dispose()
    {
        lock (_gate)
        {
            _jsonl.Flush();
            _jsonl.Dispose();
            _binary.Flush();
            _binary.Dispose();
        }
    }
}
