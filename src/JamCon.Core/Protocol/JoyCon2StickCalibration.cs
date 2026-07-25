using System.Buffers.Binary;

namespace JamCon.Core.Protocol;

/// <summary>
/// Factory stick calibration: the authoritative center and range for a Joy-Con 2.
/// Runtime neutral correction is permitted only within
/// <see cref="Gyro.GyroConstants.NeutralCorrectionRadiusCounts"/> of this immutable anchor,
/// so a long-held stick position can never walk the center.
/// </summary>
public readonly record struct JoyCon2StickCalibration(
    ushort CenterX, ushort CenterY,
    ushort PositiveRangeX, ushort PositiveRangeY,
    ushort NegativeRangeX, ushort NegativeRangeY)
{
    /// <summary>
    /// Rejects records that cannot describe a real stick. Upstream treats the calibration read as
    /// optional precisely so a malformed record cannot prevent otherwise valid input.
    /// </summary>
    public bool IsPlausible()
    {
        if (CenterX == 0 || CenterX >= 4095 || CenterY == 0 || CenterY >= 4095) return false;

        foreach (var range in (ReadOnlySpan<ushort>)[PositiveRangeX, PositiveRangeY, NegativeRangeX, NegativeRangeY])
        {
            if (range < 200 || range > 4095) return false;
        }

        return CenterX + PositiveRangeX <= 4095
            && CenterY + PositiveRangeY <= 4095
            && CenterX - NegativeRangeX >= 0
            && CenterY - NegativeRangeY >= 0;
    }
}

public static class JoyCon2StickCalibrationDecoder
{
    private const int RecordOffset = 16;
    private const int AddressOffset = 12;
    private const int LengthOffset = 8;

    /// <summary>
    /// Decodes the nine-byte factory calibration record from a memory-read response.
    /// Verifies the echoed address and length before trusting the payload, and returns false for
    /// any record that fails the plausibility check.
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> response,
        out JoyCon2StickCalibration calibration,
        uint expectedAddress = JoyCon2Ids.StickCalibrationAddress)
    {
        calibration = default;

        if (response.Length < RecordOffset + JoyCon2Ids.StickCalibrationLength) return false;
        if (response[LengthOffset] < JoyCon2Ids.StickCalibrationLength) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(response[AddressOffset..]) != expectedAddress) return false;

        var record = response.Slice(RecordOffset, JoyCon2Ids.StickCalibrationLength);
        var center = Unpack(record[..3]);
        var positive = Unpack(record[3..6]);
        var negative = Unpack(record[6..9]);

        var decoded = new JoyCon2StickCalibration(
            center.X, center.Y,
            positive.X, positive.Y,
            negative.X, negative.Y);

        if (!decoded.IsPlausible()) return false;

        calibration = decoded;
        return true;
    }

    /// <summary>Same 12-bit-pair packing used by the analog stick fields in report 0x05.</summary>
    private static StickSample Unpack(ReadOnlySpan<byte> bytes) =>
        JoyCon2ReportDecoder.DecodeStick(bytes);
}
