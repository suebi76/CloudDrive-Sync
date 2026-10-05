using System.Text.Json;
using CloudDriveSync.Core.Diagnostics;
using CloudDriveSync.Core.Errors;

namespace CloudDriveSync.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Saving is atomic (temporary file, then replace with a backup
/// copy); a damaged file is replaced by the backup, so CloudDrive-Sync always starts.
/// </summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _file;
    private readonly object _gate = new();
    private AppSettings? _current;

    public SettingsStore(string file) => _file = file;

    public event EventHandler? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate) return _current ??= Load();
        }
    }

    /// <summary>A copy of the settings, safe to read on any thread while others change them.</summary>
    public AppSettings Snapshot()
    {
        lock (_gate)
        {
            var settings = _current ??= Load();
            return JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, JsonOptions), JsonOptions) ?? new AppSettings();
        }
    }

    /// <summary>Changes the settings under a lock and saves them.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (_gate)
        {
            var settings = _current ??= Load();
            change(settings);
            Save(settings);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AppSettings Load()
    {
        foreach (var candidate in new[] { _file, _file + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), JsonOptions) ?? new AppSettings();
                if (settings.SchemaVersion > AppSettings.CurrentSchema)
                    throw new CdException("CD-2001", $"settings schema {settings.SchemaVersion} is newer than {AppSettings.CurrentSchema}");
                if (candidate != _file) Log.Warn("Settings", "Settings restored from the backup copy.");
                return settings;
            }
            catch (JsonException e)
            {
                Log.Error("Settings", $"Settings file '{Path.GetFileName(candidate)}' is damaged: {e.Message}");
            }
        }
        return new AppSettings();
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        if (File.Exists(_file)) File.Replace(temporary, _file, _file + ".bak");
        else File.Move(temporary, _file);
    }
}
