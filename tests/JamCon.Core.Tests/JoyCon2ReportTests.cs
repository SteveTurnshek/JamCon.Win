using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// Decoder tests built on synthetic frames with known values at the documented offsets.
/// These verify the offsets and endianness; they cannot verify that the offsets match real
/// hardware. Only the Phase 1 golden-vector capture can do that.
/// </summary>
public class JoyCon2ReportTests
{
    /// <summary>Builds a minimum-length frame with recognizable values at each documented offset.</summary>
    private static byte[] BuildFrame()
    {
        var frame = new byte[JoyCon2ReportDecoder.MinimumLength];

        // Buttons at 0x04, four bytes little-endian.
        frame[0x04] = 0x21;
        frame[0x05] = 0x43;
        frame[0x06] = 0x65;
        frame[0x07] = 0x87;

        // Left stick at 0x0A: x = 0x123, y = 0x456.
        frame[0x0A] = 0x23;
        frame[0x0B] = 0x61;
        frame[0x0C] = 0x45;

        // Right stick at 0x0D: x = 0x789, y = 0xABC.
        frame[0x0D] = 0x89;
        frame[0x0E] = 0xC7;
        frame[0x0F] = 0xAB;

        WriteInt16(frame, 0x30, 100);    // accel X
        WriteInt16(frame, 0x32, -200);   // accel Y
        WriteInt16(frame, 0x34, 300);    // accel Z
        WriteInt16(frame, 0x36, -400);   // gyro X
        WriteInt16(frame, 0x38, 500);    // gyro Y
        WriteInt16(frame, 0x3A, -600);   // gyro Z
        return frame;
    }

    private static void WriteInt16(byte[] buffer, int offset, short value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    [Fact]
    public void DecodesButtonsAsLittleEndianUInt32()
    {
        Assert.True(JoyCon2ReportDecoder.TryDecode(BuildFrame(), out var report));
        Assert.Equal(0x87654321u, report.Buttons);
    }

    [Fact]
    public void DecodesBothSticksFromTwelveBitPairs()
    {
        Assert.True(JoyCon2ReportDecoder.TryDecode(BuildFrame(), out var report));

        Assert.Equal(0x123, report.LeftStick.X);
        Assert.Equal(0x456, report.LeftStick.Y);
        Assert.Equal(0x789, report.RightStick.X);
        Assert.Equal(0xABC, report.RightStick.Y);
    }

    [Fact]
    public void DecodesSignedLittleEndianMotionFields()
    {
        Assert.True(JoyCon2ReportDecoder.TryDecode(BuildFrame(), out var report));

        Assert.Equal(100, report.Imu.AccelX);
        Assert.Equal(-200, report.Imu.AccelY);
        Assert.Equal(300, report.Imu.AccelZ);
        Assert.Equal(-400, report.Imu.GyroX);
        Assert.Equal(500, report.Imu.GyroY);
        Assert.Equal(-600, report.Imu.GyroZ);
    }

    [Fact]
    public void RejectsFramesShorterThanTheImuBlock()
    {
        // 0x3B bytes stops one byte short of completing gyro Z.
        Assert.False(JoyCon2ReportDecoder.TryDecode(new byte[JoyCon2ReportDecoder.MinimumLength - 1], out _));
        Assert.True(JoyCon2ReportDecoder.TryDecode(new byte[JoyCon2ReportDecoder.MinimumLength], out _));
    }

    [Fact]
    public void MinimumLengthMatchesTheDocumentedValue()
    {
        Assert.Equal(0x3C, JoyCon2ReportDecoder.MinimumLength);
    }

    [Fact]
    public void IdentifiesTheZeroFilledStartupReport()
    {
        Assert.True(JoyCon2ReportDecoder.TryDecode(new byte[JoyCon2ReportDecoder.MinimumLength], out var empty));
        Assert.True(empty.Imu.IsZeroFilled);

        Assert.True(JoyCon2ReportDecoder.TryDecode(BuildFrame(), out var populated));
        Assert.False(populated.Imu.IsZeroFilled);
    }

    [Fact]
    public void PressedBitIndicesReportsSetBitsLowestFirst()
    {
        var frame = new byte[JoyCon2ReportDecoder.MinimumLength];
        frame[0x04] = 0b0000_0101; // bits 0 and 2
        frame[0x07] = 0b1000_0000; // bit 31

        Assert.True(JoyCon2ReportDecoder.TryDecode(frame, out var report));
        Assert.Equal([0, 2, 31], report.PressedBitIndices().ToArray());
    }

    [Fact]
    public void StickDecodingIsConsistentAcrossTheFullTwelveBitRange()
    {
        // Round-trips the packing used by both the stick fields and the calibration record.
        for (var x = 0; x < 4096; x += 7)
        {
            for (var y = 0; y < 4096; y += 337)
            {
                byte[] packed =
                [
                    (byte)(x & 0xFF),
                    (byte)(((x >> 8) & 0x0F) | ((y & 0x0F) << 4)),
                    (byte)((y >> 4) & 0xFF),
                ];

                var sample = JoyCon2ReportDecoder.DecodeStick(packed);
                Assert.Equal(x, sample.X);
                Assert.Equal(y, sample.Y);
            }
        }
    }
}
