using System.IO;
using System.Text.Json;
using VoltManager.Models;

namespace VoltManager.Services;

public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _lock = new();

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null)
                {
                    if (loaded.Rules == null || loaded.Rules.Count == 0)
                        loaded.Rules = AppSettings.DefaultRules();
                    // Migrate stale repo name from pre-release installs.
                    if (loaded.UpdateRepo == "Albix4563/VoltManager")
                        loaded.UpdateRepo = "Albix4563/power_efficency";
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt settings file: fall through to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        SettingsChanged?.Invoke(Current);
    }

    public void Update(AppSettings settings)
    {
        Current = settings;
        Save();
    }
}
