using Windows.Devices.Radios;

namespace JamCon.Bluetooth;

/// <summary>Thrown when the Bluetooth radio is present but not usable.</summary>
public sealed class BluetoothUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public enum BluetoothAvailability
{
    /// <summary>A Bluetooth radio is present and switched on.</summary>
    Ready,

    /// <summary>A radio exists but is switched off, in airplane mode, or otherwise disabled.</summary>
    RadioOff,

    /// <summary>No Bluetooth radio is present on this machine.</summary>
    NoRadio,

    /// <summary>Radio state could not be queried; the reason is carried in the report.</summary>
    Unknown,
}

/// <param name="Detail">Human-readable explanation, suitable for showing to a user.</param>
public sealed record BluetoothAvailabilityReport(BluetoothAvailability Availability, string Detail)
{
    public bool IsReady => Availability == BluetoothAvailability.Ready;
}

/// <summary>
/// Checks whether Bluetooth is actually usable before attempting to scan.
/// </summary>
/// <remarks>
/// Starting an advertisement watcher against a disabled radio throws a bare
/// <c>COMException (0x800710DF, ERROR_DEVICE_NOT_AVAILABLE)</c>, which is accurate but useless to a
/// user. Checking first turns that into an actionable message. Verified on this machine: the radio
/// was switched off in Windows Settings while the adapter itself reported healthy in Device Manager,
/// so adapter health alone is not a sufficient signal.
/// </remarks>
public static class BluetoothRadioState
{
    public static async Task<BluetoothAvailabilityReport> CheckAsync()
    {
        try
        {
            var accessStatus = await Radio.RequestAccessAsync();
            if (accessStatus != RadioAccessStatus.Allowed)
            {
                return new BluetoothAvailabilityReport(
                    BluetoothAvailability.Unknown,
                    $"Access to radio state was not granted ({accessStatus}).");
            }

            var radios = await Radio.GetRadiosAsync();
            var bluetooth = radios.Where(r => r.Kind == RadioKind.Bluetooth).ToArray();

            if (bluetooth.Length == 0)
            {
                return new BluetoothAvailabilityReport(
                    BluetoothAvailability.NoRadio,
                    "No Bluetooth radio was found on this machine.");
            }

            if (bluetooth.Any(r => r.State == RadioState.On))
            {
                var name = bluetooth.First(r => r.State == RadioState.On).Name;
                return new BluetoothAvailabilityReport(
                    BluetoothAvailability.Ready, $"Bluetooth radio '{name}' is on.");
            }

            var states = string.Join(", ", bluetooth.Select(r => $"{r.Name}={r.State}"));
            return new BluetoothAvailabilityReport(
                BluetoothAvailability.RadioOff,
                $"Bluetooth is switched off. Turn it on in Settings > Bluetooth & devices. Radio state: {states}.");
        }
        catch (Exception ex)
        {
            return new BluetoothAvailabilityReport(
                BluetoothAvailability.Unknown, $"Could not query radio state: {ex.Message}");
        }
    }
}
