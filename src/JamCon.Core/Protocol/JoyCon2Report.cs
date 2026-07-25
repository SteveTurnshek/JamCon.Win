using System.Buffers.Binary;

namespace JamCon.Core.Protocol;

/// <summary>Which physical controller a report came from.</summary>
public enum JoyCon2Handedness
{
    Left,
    Right,
}

/// <summary>A 12-bit analog stick sample, in raw counts. Nominal center is 2048.</summary>
public readonly record struct StickSample(ushort X, ushort Y);

/// <summary>Raw IMU counts. Convert gyro with <see cref="Gyro.GyroConstants.DegreesPerSecondPerCount"/>.</summary>
public readonly record struct ImuSample(
    short AccelX, short AccelY, short AccelZ,
    short GyroX, short GyroY, short GyroZ)
{
    public bool IsZeroFilled =>
        AccelX == 0 && AccelY == 0 && AccelZ == 0 &&
        GyroX == 0 && GyroY == 0 && GyroZ == 0;
}

/// <summary>
/// A decoded common input report <c>0x05</c>.
/// </summary>
/// <param name="Buttons">
/// The raw four-byte button field at offset 0x04, as a little-endian uint. Bit meanings are
/// intentionally not named here: the Joy-Con-family bit grouping is applied later by the
/// mapping layer, and Phase 1 verifies the bit assignments empirically on Windows.
/// </param>
public readonly record struct JoyCon2Report(
    uint Buttons,
    StickSample LeftStick,
    StickSample RightStick,
    ImuSample Imu)
{
    /// <summary>Indices of set button bits, low bit first. Diagnostic aid for the Phase 1 probe.</summary>
    public IEnumerable<int> PressedBitIndices()
    {
        for (var bit = 0; bit < 32; bit++)
        {
            if ((Buttons & (1u << bit)) != 0) yield return bit;
        }
    }
}

/// <summary>
/// Decoder for Joy-Con 2 common input report <c>0x05</c>, per docs/JoyCon2-Bluetooth.md.
/// Offsets are transcribed from the validated Swift implementation; see
/// reference/swift/JoyCon2BLEProtocol.swift.
/// </summary>
public static class JoyCon2ReportDecoder
{
    /// <summary>
    /// Shortest accepted payload (0x3C = 60 bytes). The gyro Z field ends at 0x3B, so a
    /// shorter frame cannot carry a complete IMU sample.
    /// </summary>
    public const int MinimumLength = 0x3C;

    private const int ButtonsOffset = 0x04;
    private const int LeftStickOffset = 0x0A;
    private const int RightStickOffset = 0x0D;
    private const int AccelXOffset = 0x30;
    private const int GyroXOffset = 0x36;

    /// <summary>
    /// Decodes a notification payload. The transport delivers the report without a USB
    /// report-ID byte, so offset 0 is the first payload byte on both CoreBluetooth and WinRT.
    /// Returns false when the payload is too short to decode.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out JoyCon2Report report)
    {
        if (payload.Length < MinimumLength)
        {
            report = default;
            return false;
        }

        report = new JoyCon2Report(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[ButtonsOffset..]),
            DecodeStick(payload[LeftStickOffset..]),
            DecodeStick(payload[RightStickOffset..]),
            new ImuSample(
                AccelX: BinaryPrimitives.ReadInt16LittleEndian(payload[AccelXOffset..]),
                AccelY: BinaryPrimitives.ReadInt16LittleEndian(payload[(AccelXOffset + 2)..]),
                AccelZ: BinaryPrimitives.ReadInt16LittleEndian(payload[(AccelXOffset + 4)..]),
                GyroX: BinaryPrimitives.ReadInt16LittleEndian(payload[GyroXOffset..]),
                GyroY: BinaryPrimitives.ReadInt16LittleEndian(payload[(GyroXOffset + 2)..]),
                GyroZ: BinaryPrimitives.ReadInt16LittleEndian(payload[(GyroXOffset + 4)..])));
        return true;
    }

    /// <summary>
    /// Unpacks two 12-bit stick axes from three bytes. Same packing as the factory
    /// calibration record: X is byte0 plus the low nibble of byte1, Y is the high nibble
    /// of byte1 plus byte2.
    /// </summary>
    public static StickSample DecodeStick(ReadOnlySpan<byte> bytes) => new(
        (ushort)(bytes[0] | ((bytes[1] & 0x0F) << 8)),
        (ushort)((bytes[1] >> 4) | (bytes[2] << 4)));
}
