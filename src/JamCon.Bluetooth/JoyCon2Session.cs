using System.Diagnostics;
using JamCon.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace JamCon.Bluetooth;

/// <summary>How the GATT connection was ultimately established.</summary>
public enum Jc2ConnectPath
{
    /// <summary>GATT worked against an unpaired device, as CoreBluetooth does.</summary>
    UnpairedGatt,

    /// <summary>Unpaired GATT failed; the device had to be paired first.</summary>
    PairedAfterFailure,

    /// <summary>The device was already paired with the OS before we started.</summary>
    AlreadyPaired,
}

/// <summary>A single decoded notification, with a monotonic timestamp.</summary>
public sealed record Jc2Frame(byte[] Payload, double TimestampSeconds, JoyCon2Report? Report);

/// <summary>
/// A live GATT session with one Joy-Con 2: connect, run the validated initialization
/// sequence, then stream common input report <c>0x05</c>.
/// </summary>
/// <remarks>
/// The initialization order is transcribed from reference/swift/JoyCon2BLESession.swift and must
/// not be rearranged casually. In particular the command-response subscription and the report-rate
/// descriptor write both happen <i>before</i> input notifications are enabled.
/// </remarks>
public sealed class JoyCon2Session : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(1);
    private const int CommandAttempts = 3;

    private readonly ulong _address;
    private readonly Action<string> _log;

    private BluetoothLEDevice? _device;
    private GattSession? _gattSession;
    private GattCharacteristic? _commandWrite;
    private GattCharacteristic? _commandResponse;
    private GattCharacteristic? _commonInput;
    private GattDescriptor? _reportRateDescriptor;

    private TaskCompletionSource<byte[]>? _pendingResponse;
    private readonly Stopwatch _clock = new();
    private readonly int _discoveryAttempts;
    private readonly bool _allowPairing;

    /// <param name="discoveryAttempts">
    /// Service-discovery attempts, about three seconds apart, before giving up. The probe uses a
    /// long window so a person has time to wake the controller; a reconnect loop wants a short one
    /// and retries at its own level.
    /// </param>
    /// <param name="allowPairing">
    /// Whether to fall back to OS pairing when unpaired GATT fails. Off by default because hardware
    /// showed pairing <i>breaks</i> the Joy-Con 2 link on Windows (docs/Windows-Port.md, question 1).
    /// Kept only so the experiment can be repeated.
    /// </param>
    public JoyCon2Session(ulong address, Action<string> log, int discoveryAttempts = 40, bool allowPairing = false)
    {
        _address = address;
        _log = log;
        _discoveryAttempts = discoveryAttempts;
        _allowPairing = allowPairing;
    }

    /// <summary>Raised for every common-input notification.</summary>
    public event Action<Jc2Frame>? FrameReceived;

    /// <summary>Raised when the connection status changes, with true meaning connected.</summary>
    public event Action<bool>? ConnectionChanged;

    public Jc2ConnectPath ConnectPath { get; private set; }
    public JoyCon2Handedness Handedness { get; private set; }
    public JoyCon2StickCalibration? Calibration { get; private set; }
    public JoyCon2ReportRate? NegotiatedReportRate { get; private set; }
    public string DeviceName => JoyCon2Advertisement.DisplayName(Handedness);

    /// <summary>
    /// True when Windows already had the controller paired. On Joy-Con 2 that state is known to stop
    /// GATT from working, so callers should tell the user to remove it in Bluetooth settings.
    /// </summary>
    public bool DeviceWasPaired { get; private set; }

    public async Task<bool> ConnectAsync(JoyCon2Handedness handedness, CancellationToken ct)
    {
        Handedness = handedness;

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address);
        if (_device is null)
        {
            _log("FromBluetoothAddressAsync returned null — the address is not known to the radio.");
            return false;
        }

        var wasPaired = _device.DeviceInformation.Pairing.IsPaired;
        DeviceWasPaired = wasPaired;
        _log($"Device object acquired. Already paired: {wasPaired}. Status: {_device.ConnectionStatus}.");

        await RequestThroughputOptimizedParametersAsync();
        await OpenGattSessionAsync();

        var service = await FindServiceWithRetryAsync(ct, _discoveryAttempts);

        if (service is null && !wasPaired && _allowPairing)
        {
            // Experimental only: hardware showed pairing breaks the link rather than enabling it.
            _log("Unpaired GATT failed. Attempting to pair, then retrying service discovery.");
            if (!await TryPairAsync(ct)) return false;

            service = await FindServiceWithRetryAsync(ct, _discoveryAttempts);
            ConnectPath = Jc2ConnectPath.PairedAfterFailure;
        }
        else
        {
            ConnectPath = wasPaired ? Jc2ConnectPath.AlreadyPaired : Jc2ConnectPath.UnpairedGatt;
        }

        if (service is null)
        {
            _log("Joy-Con 2 GATT service not found, paired or otherwise.");
            return false;
        }

        _log($"Service found via {ConnectPath}.");

        if (!await AcquireCharacteristicsAsync(service)) return false;

        _clock.Restart();
        _device.ConnectionStatusChanged += OnConnectionStatusChanged;

        if (!await SubscribeCommandResponsesAsync()) return false;
        await AcquireReportRateDescriptorAsync();
        if (!await RunInitializationSequenceAsync(ct)) return false;
        await ConfigureReportRateAsync();
        return await SubscribeCommonInputAsync();
    }

    /// <summary>
    /// Asks Windows for the shortest connection interval it will grant. This is the closest
    /// analogue to the CoreBluetooth interval override that upstream needed to reach 66 Hz;
    /// without it Windows is free to pick a slower interval and halve the delivered rate.
    /// </summary>
    private async Task RequestThroughputOptimizedParametersAsync()
    {
        try
        {
            var request = _device!.RequestPreferredConnectionParameters(
                BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            _log($"RequestPreferredConnectionParameters(ThroughputOptimized) -> {request.Status}.");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _log($"RequestPreferredConnectionParameters unavailable or rejected: {ex.Message}");
        }
    }

    /// <summary>
    /// Holds the link open. Without MaintainConnection, Windows may drop an idle GATT client and
    /// the notification stream stops without an obvious error.
    /// </summary>
    private async Task OpenGattSessionAsync()
    {
        try
        {
            _gattSession = await GattSession.FromDeviceIdAsync(BluetoothDeviceId.FromId(_device!.DeviceId));
            _gattSession.MaintainConnection = true;
            _log($"GattSession opened. MaxPduSize: {_gattSession.MaxPduSize}.");
        }
        catch (Exception ex)
        {
            _log($"GattSession unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Retries service discovery while waiting for the link to come up.
    /// </summary>
    /// <remarks>
    /// A paired-but-disconnected BLE device reports <c>Unreachable</c> rather than connecting
    /// instantly: Windows only establishes the link lazily, and a sleeping Joy-Con 2 will not
    /// respond at all until a button wakes its radio. Discovering once and giving up reports a
    /// perfectly good controller as missing, which is exactly what happened on the first hardware
    /// run. `MaintainConnection` is already set, so each retry gives Windows another chance to
    /// complete the connection it has been asked to hold open.
    /// </remarks>
    private async Task<GattDeviceService?> FindServiceWithRetryAsync(
        CancellationToken ct, int attempts = 40, int delayMilliseconds = 3000)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var service = await FindServiceAsync();
            if (service is not null)
            {
                // The ATT MTU is negotiated on connect, so the useful value is only available
                // now. Report 0x05 needs 60 bytes, which a 23-byte default PDU cannot carry.
                _log($"Connected. Status: {_device!.ConnectionStatus}. " +
                     $"MaxPduSize now: {_gattSession?.MaxPduSize.ToString() ?? "unknown"}.");
                return service;
            }

            if (attempt == attempts) break;

            _log($"Service unreachable (attempt {attempt}/{attempts}, status {_device!.ConnectionStatus}). " +
                 "PRESS ANY BUTTON ON THE CONTROLLER to wake its radio. Retrying...");

            // Wake early the moment Windows reports the link up. The controller has been seen
            // flapping between connected and disconnected while uninitialized, so a connected
            // window can be shorter than the polling delay and would otherwise be missed.
            if (!await WaitForConnectedOrDelayAsync(delayMilliseconds, ct)) break;
        }

        return null;
    }

    /// <summary>
    /// Waits until the device reports <see cref="BluetoothConnectionStatus.Connected"/> or the delay
    /// elapses, whichever comes first. Returns false only when cancelled.
    /// </summary>
    private async Task<bool> WaitForConnectedOrDelayAsync(int delayMilliseconds, CancellationToken ct)
    {
        var device = _device!;
        if (device.ConnectionStatus == BluetoothConnectionStatus.Connected) return true;

        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(BluetoothLEDevice sender, object _)
        {
            _log($"Connection status changed: {sender.ConnectionStatus}.");
            if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected) connected.TrySetResult();
        }

        device.ConnectionStatusChanged += OnStatus;
        try
        {
            await Task.WhenAny(connected.Task, Task.Delay(delayMilliseconds, ct));
            return !ct.IsCancellationRequested;
        }
        finally
        {
            device.ConnectionStatusChanged -= OnStatus;
        }
    }

    private async Task<GattDeviceService?> FindServiceAsync()
    {
        try
        {
            var result = await _device!.GetGattServicesForUuidAsync(
                JoyCon2Ids.Service, BluetoothCacheMode.Uncached);
            _log($"Service discovery status: {result.Status}, services: {result.Services.Count}.");
            return result.Status == GattCommunicationStatus.Success && result.Services.Count > 0
                ? result.Services[0]
                : null;
        }
        catch (Exception ex)
        {
            _log($"Service discovery threw: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> TryPairAsync(CancellationToken ct)
    {
        var pairing = _device!.DeviceInformation.Pairing;
        if (!pairing.CanPair)
        {
            _log("Device reports CanPair = false.");
            return false;
        }

        var custom = pairing.Custom;
        void OnPairingRequested(DeviceInformationCustomPairing _, DevicePairingRequestedEventArgs e) => e.Accept();
        custom.PairingRequested += OnPairingRequested;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.None);
            _log($"PairAsync -> {result.Status}.");
            var ok = result.Status is DevicePairingResultStatus.Paired
                or DevicePairingResultStatus.AlreadyPaired;

            if (ok)
            {
                // The device object caches pairing state; re-acquire so discovery sees the new state.
                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(_address);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }

            return ok;
        }
        catch (Exception ex)
        {
            _log($"PairAsync threw: {ex.Message}");
            return false;
        }
        finally
        {
            custom.PairingRequested -= OnPairingRequested;
        }
    }

    private async Task<bool> AcquireCharacteristicsAsync(GattDeviceService service)
    {
        _commandWrite = await FindCharacteristicAsync(service, JoyCon2Ids.CommandWrite, nameof(JoyCon2Ids.CommandWrite));
        _commandResponse = await FindCharacteristicAsync(service, JoyCon2Ids.CommandResponse, nameof(JoyCon2Ids.CommandResponse));
        _commonInput = await FindCharacteristicAsync(service, JoyCon2Ids.CommonInput, nameof(JoyCon2Ids.CommonInput));
        return _commandWrite is not null && _commandResponse is not null && _commonInput is not null;
    }

    private async Task<GattCharacteristic?> FindCharacteristicAsync(
        GattDeviceService service, Guid uuid, string label)
    {
        var result = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            _log($"Characteristic {label} not found ({result.Status}).");
            return null;
        }

        return result.Characteristics[0];
    }

    private async Task<bool> SubscribeCommandResponsesAsync()
    {
        _commandResponse!.ValueChanged += OnCommandResponse;
        var status = await _commandResponse.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        _log($"Command-response subscription: {status}.");
        return status == GattCommunicationStatus.Success;
    }

    private async Task AcquireReportRateDescriptorAsync()
    {
        var result = await _commonInput!.GetDescriptorsForUuidAsync(
            JoyCon2Ids.ReportRateDescriptor, BluetoothCacheMode.Uncached);

        if (result.Status == GattCommunicationStatus.Success && result.Descriptors.Count > 0)
        {
            _reportRateDescriptor = result.Descriptors[0];
            _log("Report-rate descriptor found.");
        }
        else
        {
            _log($"No report-rate descriptor ({result.Status}); the controller's default cadence will apply.");
        }
    }

    private async Task<bool> RunInitializationSequenceAsync(CancellationToken ct)
    {
        // Order matters and is transcribed from the validated implementation. Note that the
        // documented "enable buttons, stick and IMU" step is actually two commands.
        if (!(await SendCommandAsync(JoyCon2Commands.SetFeatureMask.ToArray(), "setFeatureMask", true, ct)).Success)
            return false;
        if (!(await SendCommandAsync(JoyCon2Commands.EnableFeatures.ToArray(), "enableFeatures", true, ct)).Success)
            return false;

        // Optional by design: a malformed or changed record must not block otherwise valid input.
        var calibrationResult = await SendCommandAsync(
            JoyCon2Commands.ReadPrimaryStickCalibration(), "readStickCalibration", false, ct);
        if (calibrationResult is { Success: true, Response: not null })
        {
            if (JoyCon2StickCalibrationDecoder.TryDecode(calibrationResult.Response, out var calibration))
            {
                Calibration = calibration;
                _log($"Factory stick calibration: center=({calibration.CenterX},{calibration.CenterY}) " +
                     $"+=({calibration.PositiveRangeX},{calibration.PositiveRangeY}) " +
                     $"-=({calibration.NegativeRangeX},{calibration.NegativeRangeY}).");
            }
            else
            {
                _log("Stick calibration record present but implausible; ignoring it.");
            }
        }

        return (await SendCommandAsync(JoyCon2Commands.SetPlayerOneLed.ToArray(), "setPlayerOneLED", true, ct)).Success;
    }

    private async Task<(bool Success, byte[]? Response)> SendCommandAsync(
        byte[] payload, string label, bool required, CancellationToken ct)
    {
        var writeOption = _commandWrite!.CharacteristicProperties.HasFlag(
            GattCharacteristicProperties.WriteWithoutResponse)
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

        for (var attempt = 1; attempt <= CommandAttempts; attempt++)
        {
            var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponse = pending;

            var status = await _commandWrite.WriteValueAsync(ToBuffer(payload), writeOption);
            if (status != GattCommunicationStatus.Success)
            {
                _log($"{label}: write failed ({status}) on attempt {attempt}.");
                continue;
            }

            // The GATT write acknowledgment only confirms transport. The controller's response
            // notification is the semantic acknowledgment, so wait for that.
            var completed = await Task.WhenAny(pending.Task, Task.Delay(CommandTimeout, ct));
            if (completed != pending.Task)
            {
                _log($"{label}: no response within {CommandTimeout.TotalMilliseconds:F0} ms on attempt {attempt}.");
                continue;
            }

            var response = await pending.Task;
            switch (JoyCon2Commands.ResponseSucceeded(response, payload))
            {
                case true:
                    _log($"{label}: acknowledged.");
                    return (true, response);
                case false:
                    _log($"{label}: controller reported failure on attempt {attempt}.");
                    continue;
                default:
                    _log($"{label}: response did not match the command on attempt {attempt}.");
                    continue;
            }
        }

        _log($"{label}: giving up after {CommandAttempts} attempts ({(required ? "required" : "optional")}).");
        return (false, null);
    }

    private async Task ConfigureReportRateAsync()
    {
        if (_reportRateDescriptor is null) return;

        // 133 Hz first: upstream measured that requesting 133 is what makes the common stream
        // actually deliver ~66. Fall back to 66 if the controller rejects it.
        foreach (var rate in (JoyCon2ReportRate[])[JoyCon2ReportRate.Hz133, JoyCon2ReportRate.Hz66])
        {
            try
            {
                var status = await _reportRateDescriptor.WriteValueAsync(
                    ToBuffer(JoyCon2Commands.ReportRateDescriptorValue(rate)));
                _log($"Report-rate request {(ushort)rate:X4} ({rate}): {status}.");
                if (status == GattCommunicationStatus.Success)
                {
                    NegotiatedReportRate = rate;
                    return;
                }
            }
            catch (Exception ex)
            {
                _log($"Report-rate request {rate} threw: {ex.Message}");
            }
        }
    }

    private async Task<bool> SubscribeCommonInputAsync()
    {
        _commonInput!.ValueChanged += OnCommonInput;
        var status = await _commonInput.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        _log($"Common-input subscription: {status}.");
        return status == GattCommunicationStatus.Success;
    }

    private void OnCommandResponse(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = FromBuffer(args.CharacteristicValue);
        _pendingResponse?.TrySetResult(bytes);
    }

    private void OnCommonInput(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var payload = FromBuffer(args.CharacteristicValue);
        var timestamp = (double)_clock.ElapsedTicks / Stopwatch.Frequency;
        JoyCon2Report? report = JoyCon2ReportDecoder.TryDecode(payload, out var decoded) ? decoded : null;
        FrameReceived?.Invoke(new Jc2Frame(payload, timestamp, report));
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) =>
        ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

    private static IBuffer ToBuffer(byte[] bytes) => BufferConvert.FromBytes(bytes);

    private static byte[] FromBuffer(IBuffer buffer) => BufferConvert.ToBytes(buffer);

    public async ValueTask DisposeAsync()
    {
        if (_commonInput is not null) _commonInput.ValueChanged -= OnCommonInput;
        if (_commandResponse is not null) _commandResponse.ValueChanged -= OnCommandResponse;
        if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        _gattSession?.Dispose();
        _device?.Dispose();
        await Task.CompletedTask;
    }
}
