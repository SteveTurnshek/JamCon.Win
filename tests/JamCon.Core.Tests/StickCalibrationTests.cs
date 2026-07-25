using JamCon.Core.Protocol;

namespace JamCon.Core.Tests;

/// <summary>
/// The calibration read is optional by design, so the decoder's job is as much about rejecting bad
/// records as decoding good ones: a malformed record must not be allowed to anchor the stick center.
/// </summary>
public class StickCalibrationTests
{
    /// <summary>
    /// Builds a memory-read response: 8 bytes of header, length at offset 8, the echoed address at
    /// offset 12, then the nine-byte record at offset 16.
    /// </summary>
    private static byte[] BuildResponse(
        (int X, int Y) center,
        (int X, int Y) positive,
        (int X, int Y) negative,
        uint address = JoyCon2Ids.StickCalibrationAddress,
        byte length = JoyCon2Ids.StickCalibrationLength)
    {
        var response = new byte[16 + 9];
        response[0] = 0x02;
        response[8] = length;

        response[12] = (byte)(address & 0xFF);
        response[13] = (byte)((address >> 8) & 0xFF);
        response[14] = (byte)((address >> 16) & 0xFF);
        response[15] = (byte)((address >> 24) & 0xFF);

        Pack(response, 16, center);
        Pack(response, 19, positive);
        Pack(response, 22, negative);
        return response;
    }

    private static void Pack(byte[] buffer, int offset, (int X, int Y) pair)
    {
        buffer[offset] = (byte)(pair.X & 0xFF);
        buffer[offset + 1] = (byte)(((pair.X >> 8) & 0x0F) | ((pair.Y & 0x0F) << 4));
        buffer[offset + 2] = (byte)((pair.Y >> 4) & 0xFF);
    }

    [Fact]
    public void DecodesAPlausibleFactoryRecord()
    {
        var response = BuildResponse((2048, 2048), (1600, 1600), (1600, 1600));

        Assert.True(JoyCon2StickCalibrationDecoder.TryDecode(response, out var calibration));
        Assert.Equal(2048, calibration.CenterX);
        Assert.Equal(2048, calibration.CenterY);
        Assert.Equal(1600, calibration.PositiveRangeX);
        Assert.Equal(1600, calibration.NegativeRangeY);
    }

    [Fact]
    public void RejectsAResponseEchoingADifferentAddress()
    {
        var response = BuildResponse((2048, 2048), (1600, 1600), (1600, 1600), address: 0x0001_3000);
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response, out _));
    }

    [Fact]
    public void RejectsAResponseReportingTooFewBytes()
    {
        var response = BuildResponse((2048, 2048), (1600, 1600), (1600, 1600), length: 8);
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response, out _));
    }

    [Fact]
    public void RejectsATruncatedResponse()
    {
        var response = BuildResponse((2048, 2048), (1600, 1600), (1600, 1600));
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response[..20], out _));
    }

    [Fact]
    public void RejectsAllZeroRecord()
    {
        // The failure mode that matters: a zero-filled record would otherwise anchor the center at 0.
        var response = BuildResponse((0, 0), (0, 0), (0, 0));
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response, out _));
    }

    [Fact]
    public void RejectsRangesThatOverflowTheTwelveBitAxis()
    {
        var response = BuildResponse((4000, 2048), (1600, 1600), (1600, 1600));
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response, out _));
    }

    [Fact]
    public void RejectsRangesThatUnderflowBelowZero()
    {
        var response = BuildResponse((300, 2048), (1600, 1600), (1600, 1600));
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response, out _));
    }

    [Fact]
    public void RejectsImplausiblySmallRanges()
    {
        var response = BuildResponse((2048, 2048), (100, 1600), (1600, 1600));
        Assert.False(JoyCon2StickCalibrationDecoder.TryDecode(response, out _));
    }
}
