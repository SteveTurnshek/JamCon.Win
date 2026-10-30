using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using JamCon.Bluetooth;
using JamCon.Core.Engine;
using JamCon.Core.Protocol;

namespace JamCon.App.Services;

/// <summary>
/// %LOCALAPPDATA%\JamCon.Win\settings.json: the remembered controller and the engine's
/// <see cref="InputSettings"/> (gyro tuning, button mappings, the mouse-control switch).
/// </summary>
/// <remarks>
/// There is no settings UI yet, so the file is written indented with enum names, readable and
/// editable by hand. Each section loads independently: a damaged section falls back to its
/// defaults, logged, rather than costing everything else or crashing startup.
/// </remarks>
internal sealed class AppSettingsFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private KnownJoyCon2? _controller;
    private InputSettings _input;
    private int _idleSleepMinutes = DefaultIdleSleepMinutes;
    private bool _smoothMotion = true;

    public const int DefaultIdleSleepMinutes = 10;

    public AppSettingsFile(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        (_controller, _input) = Load();
    }

    /// <summary>Disconnect an unused controller after this many minutes so it can sleep. 0 disables.</summary>
    public int IdleSleepMinutes
    {
        get
        {
            lock (_gate) return _idleSleepMinutes;
        }
    }

    /// <summary>Spread controller motion out to a high output rate. Costs ~7.5 ms of latency.</summary>
    public bool SmoothMotion
    {
        get
        {
            lock (_gate) return _smoothMotion;
        }
    }

    public KnownJoyCon2? Controller
    {
        get
        {
            lock (_gate) return _controller;
        }
    }

    public InputSettings Input
    {
        get
        {
            lock (_gate) return _input;
        }
    }

    public void Remember(KnownJoyCon2 controller)
    {
        lock (_gate)
        {
            if (_controller == controller) return;
            _controller = controller;
            Save();
        }

        _log($"Remembering {controller.DisplayName} {JoyCon2Link.Format(controller.Address)}.");
    }

    public void Forget()
    {
        lock (_gate)
        {
            _controller = null;
            Save();
        }

        _log("Forgot the remembered controller.");
    }

    public void SaveInput(InputSettings input)
    {
        lock (_gate)
        {
            _input = input;
            Save();
        }
    }

    private (KnownJoyCon2?, InputSettings) Load()
    {
        if (!File.Exists(_path)) return (null, InputSettings.Default);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A corrupt file should cost the user one SYNC press, not a crash on startup.
            _log($"Ignoring unreadable settings file {_path}: {ex.Message}");
            return (null, InputSettings.Default);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("IdleSleepMinutes", out var idle) && idle.TryGetInt32(out var minutes) && minutes >= 0)
            {
                _idleSleepMinutes = minutes;
            }

            if (root.TryGetProperty("SmoothMotion", out var smooth) && smooth.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                _smoothMotion = smooth.GetBoolean();
            }

            return (LoadController(root), LoadInput(root));
        }
    }

    private KnownJoyCon2? LoadController(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("ControllerAddress", out var address) || address.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("ControllerHandedness", out var side)
                || !Enum.TryParse<JoyCon2Handedness>(side.GetString(), out var handedness))
            {
                return null;
            }

            return new KnownJoyCon2(Convert.ToUInt64(address.GetString()!.Replace(":", ""), 16), handedness);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        {
            _log($"Ignoring unreadable remembered controller: {ex.Message}");
            return null;
        }
    }

    private InputSettings LoadInput(JsonElement root)
    {
        if (!root.TryGetProperty("Input", out var input)) return InputSettings.Default;
        try
        {
            return input.Deserialize<InputSettings>(JsonOptions) ?? InputSettings.Default;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _log($"Ignoring unreadable input settings, using defaults: {ex.Message}");
            return InputSettings.Default;
        }
    }

    private void Save()
    {
        var file = new SettingsFileContents
        {
            ControllerAddress = _controller is { } c ? JoyCon2Link.Format(c.Address) : null,
            ControllerHandedness = _controller?.Handedness.ToString(),
            Input = _input,
            IdleSleepMinutes = _idleSleepMinutes,
            SmoothMotion = _smoothMotion,
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (IOException ex)
        {
            _log($"Could not save settings: {ex.Message}");
        }
    }

    private sealed class SettingsFileContents
    {
        public string? ControllerAddress { get; set; }
        public string? ControllerHandedness { get; set; }
        public int IdleSleepMinutes { get; set; } = DefaultIdleSleepMinutes;
        public bool SmoothMotion { get; set; } = true;
        public InputSettings? Input { get; set; }
    }
}
