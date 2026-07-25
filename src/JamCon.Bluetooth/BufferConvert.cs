using Windows.Storage.Streams;

namespace JamCon.Bluetooth;

/// <summary>
/// Conversions between <see cref="IBuffer"/> and byte arrays.
/// </summary>
/// <remarks>
/// WinRT hands GATT payloads over as <see cref="IBuffer"/>, which has no array conversion of its
/// own on .NET — the <c>System.Runtime.InteropServices.WindowsRuntime</c> extensions that used to
/// provide one are not available here. <see cref="DataReader"/> and <see cref="DataWriter"/> are
/// the portable route.
/// </remarks>
internal static class BufferConvert
{
    internal static IBuffer FromBytes(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    internal static byte[] ToBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
