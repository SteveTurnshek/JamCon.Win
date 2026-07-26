using JamCon.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace JamCon.Bluetooth;

/// <summary>A Joy-Con 2 already known to Windows, found without relying on an advertisement.</summary>
public sealed record PairedJoyCon2(
    ulong BluetoothAddress,
    string DeviceId,
    string Name,
    JoyCon2Handedness Handedness,
    bool ServiceConfirmed,
    bool IsConnected);

/// <summary>
/// Finds Joy-Con 2 controllers that Windows has already paired.
/// </summary>
/// <remarks>
/// Advertisement scanning only works while the controller is in its pairing window, which is short.
/// Once a controller is paired to Windows it may stop advertising discoverably and simply reconnect,
/// so a scan-only probe would report "no Joy-Con 2 found" for a controller sitting right there and
/// already known to the OS. Enumerating paired devices covers that case.
/// </remarks>
public static class PairedJoyCon2Finder
{
    /// <summary>
    /// Enumerates paired BLE devices and returns those that look like a Joy-Con 2. When
    /// <paramref name="confirmService"/> is true each candidate is opened and checked for the
    /// Joy-Con 2 GATT service, which is authoritative but slower and wakes the radio.
    /// </summary>
    public static async Task<IReadOnlyList<PairedJoyCon2>> FindAsync(
        Action<string> log, bool confirmService = true)
    {
        var results = new List<PairedJoyCon2>();

        DeviceInformationCollection devices;
        try
        {
            devices = await DeviceInformation.FindAllAsync(
                BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        }
        catch (Exception ex)
        {
            log($"Could not enumerate paired BLE devices: {ex.Message}");
            return results;
        }

        log($"Paired BLE devices known to Windows: {devices.Count}.");

        foreach (var info in devices)
        {
            var looksRelevant = info.Name.Contains("joy", StringComparison.OrdinalIgnoreCase)
                || info.Name.Contains("nintendo", StringComparison.OrdinalIgnoreCase);

            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device is null) continue;

                var confirmed = false;
                if (confirmService || looksRelevant)
                {
                    var services = await device.GetGattServicesForUuidAsync(
                        JoyCon2Ids.Service, BluetoothCacheMode.Cached);
                    confirmed = services.Status == GattCommunicationStatus.Success
                        && services.Services.Count > 0;
                }

                if (!confirmed && !looksRelevant) continue;

                // Handedness is not recoverable without manufacturer data; the name is the only
                // hint available here, and the session re-derives nothing from it.
                var handedness = info.Name.Contains("(L)", StringComparison.OrdinalIgnoreCase)
                    || info.Name.Contains("left", StringComparison.OrdinalIgnoreCase)
                        ? JoyCon2Handedness.Left
                        : JoyCon2Handedness.Right;

                results.Add(new PairedJoyCon2(
                    device.BluetoothAddress,
                    info.Id,
                    string.IsNullOrWhiteSpace(info.Name) ? "(unnamed)" : info.Name,
                    handedness,
                    confirmed,
                    device.ConnectionStatus == BluetoothConnectionStatus.Connected));
            }
            catch (Exception ex)
            {
                log($"Could not inspect paired device '{info.Name}': {ex.Message}");
            }
            finally
            {
                device?.Dispose();
            }
        }

        return results;
    }
}
