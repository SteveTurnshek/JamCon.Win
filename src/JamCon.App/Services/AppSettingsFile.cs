using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// The file is written indented with enum names, readable and editable by hand. Button layouts and
/// the radial menu are saved only once they differ from the defaults, so improved defaults still
/// reach anyone who never customised them. Each section loads independently: a damaged section falls back to its
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
    private readonly Dictionary<JoyCon2Handedness, KnownJoyCon2> _controllers = [];
    private InputSettings _input;
    private int _idleSleepMinutes = DefaultIdleSleepMinutes;
    private bool _smoothMotion = true;

    public const int DefaultIdleSleepMinutes = 10;

    public AppSettingsFile(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        _input = Load();
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

    public void SetIdleSleepMinutes(int minutes)
    {
        lock (_gate)
        {
            _idleSleepMinutes = Math.Max(0, minutes);
            Save();
        }
    }

    public void SetSmoothMotion(bool enabled)
    {
        lock (_gate)
        {
            _smoothMotion = enabled;
            Save();
        }
    }

    /// <summary>The remembered controller for one hand, if any. A pair is remembered as one of each.</summary>
    public KnownJoyCon2? ControllerFor(JoyCon2Handedness side)
    {
        lock (_gate) return _controllers.TryGetValue(side, out var c) ? c : null;
    }

    public bool HasControllers
    {
        get
        {
            lock (_gate) return _controllers.Count > 0;
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
            if (_controllers.TryGetValue(controller.Handedness, out var current) && current == controller) return;
            _controllers[controller.Handedness] = controller;
            Save();
        }

        _log($"Remembering {controller.DisplayName} {JoyCon2Link.Format(controller.Address)}.");
    }

    public void Forget()
    {
        lock (_gate)
        {
            _controllers.Clear();
            Save();
        }

        _log("Forgot the remembered controllers.");
    }

    public void SaveInput(InputSettings input)
    {
        lock (_gate)
        {
            _input = input;
            Save();
        }
    }

    private InputSettings Load()
    {
        if (!File.Exists(_path)) return InputSettings.Default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(_path));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // A corrupt file should cost the user one SYNC press, not a crash on startup.
            _log($"Ignoring unreadable settings file {_path}: {ex.Message}");
            return InputSettings.Default;
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

            LoadControllers(root);
            return LoadInput(root);
        }
    }

    /// <summary>Reads <c>Controllers</c>, or the single <c>ControllerAddress</c> that files written before pairs had.</summary>
    private void LoadControllers(JsonElement root)
    {
        if (root.TryGetProperty("Controllers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (LoadController(entry, "Address", "Handedness") is { } c) _controllers[c.Handedness] = c;
            }
        }
        else if (LoadController(root, "ControllerAddress", "ControllerHandedness") is { } legacy)
        {
            _controllers[legacy.Handedness] = legacy;
        }
    }

    private KnownJoyCon2? LoadController(JsonElement element, string addressKey, string sideKey)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty(addressKey, out var address) || address.ValueKind != JsonValueKind.String
                || !element.TryGetProperty(sideKey, out var side)
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
            Controllers = _controllers.Values
                .OrderBy(c => c.Handedness)
                .Select(c => new RememberedController(JoyCon2Link.Format(c.Address), c.Handedness.ToString()))
                .ToList(),
            Input = InputNode(_input),
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

    private static readonly string[] SavedOnlyWhenChanged =
        [nameof(InputSettings.RightButtons), nameof(InputSettings.LeftButtons),
         nameof(InputSettings.GameRightButtons), nameof(InputSettings.GameLeftButtons),
         nameof(InputSettings.GamePairRightButtons), nameof(InputSettings.GamePairLeftButtons),
         nameof(InputSettings.ControllerRightButtons), nameof(InputSettings.ControllerLeftButtons),
         nameof(InputSettings.ControllerPairRightButtons), nameof(InputSettings.ControllerPairLeftButtons),
         nameof(InputSettings.RadialMenu)];

    private static JsonNode InputNode(InputSettings input)
    {
        var node = JsonSerializer.SerializeToNode(input, JsonOptions)!.AsObject();
        var defaults = JsonSerializer.SerializeToNode(InputSettings.Default, JsonOptions)!.AsObject();
        foreach (var name in SavedOnlyWhenChanged)
        {
            if (JsonNode.DeepEquals(node[name], defaults[name])) node.Remove(name);
        }

        return node;
    }

    private sealed record RememberedController(string Address, string Handedness);

    private sealed class SettingsFileContents
    {
        public List<RememberedController> Controllers { get; set; } = [];
        public int IdleSleepMinutes { get; set; } = DefaultIdleSleepMinutes;
        public bool SmoothMotion { get; set; } = true;
        public JsonNode? Input { get; set; }
    }
}
