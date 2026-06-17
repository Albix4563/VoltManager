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
                    if (loaded.AutoShutdown == null)
                        loaded.AutoShutdown = new AutoShutdownSettings();
                    if (loaded.AutoUpdates == null)
                        loaded.AutoUpdates = new AutoUpdateSettings();
                    if (loaded.HeavyAppDetection == null)
                        loaded.HeavyAppDetection = new HeavyAppDetectionSettings();
                    if (loaded.AppPowerProfiles == null)
                        loaded.AppPowerProfiles = new AppPowerProfileSettings();
                    if (loaded.KeepAwake == null)
                        loaded.KeepAwake = new KeepAwakeSettings();
                    NormalizeScheduledPowerAction(loaded.AutoShutdown);
                    NormalizeAutoUpdateSettings(loaded.AutoUpdates);
                    NormalizeHeavyAppDetectionSettings(loaded.HeavyAppDetection);
                    NormalizeAppPowerProfileSettings(loaded.AppPowerProfiles);
                    NormalizeKeepAwakeSettings(loaded.KeepAwake);
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

    private static void NormalizeScheduledPowerAction(AutoShutdownSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Time))
            settings.Time = "23:00";

        settings.Action = settings.Action switch
        {
            "shutdown" or "restart" or "sleep" => settings.Action,
            _ => "shutdown",
        };
    }

    private static void NormalizeAutoUpdateSettings(AutoUpdateSettings settings)
    {
        if (settings.IntervalMinutes < 5)
            settings.IntervalMinutes = 30;
        else if (settings.IntervalMinutes > 1440)
            settings.IntervalMinutes = 1440;

        if (!string.IsNullOrWhiteSpace(settings.SkippedVersion))
            settings.SkippedVersion = settings.SkippedVersion.Trim().TrimStart('v', 'V');

        settings.UpdateChannel = settings.UpdateChannel switch
        {
            "stable" or "preview" or "dev" => settings.UpdateChannel,
            _ => "stable",
        };
    }

    private static void NormalizeHeavyAppDetectionSettings(HeavyAppDetectionSettings settings)
    {
        settings.MinWorkingSetMb = Math.Clamp(settings.MinWorkingSetMb, 256, 8192);

        if (!settings.UseWindowsGpuPreferences && !settings.UseGameInstallHeuristics && !settings.UseResourceHeuristics)
            settings.UseWindowsGpuPreferences = true;
    }

    private static void NormalizeAppPowerProfileSettings(AppPowerProfileSettings settings)
    {
        settings.Rules ??= new List<AppPowerProfileRule>();

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<AppPowerProfileRule>();
        foreach (var rule in settings.Rules)
        {
            if (rule == null) continue;

            rule.Path = Environment.ExpandEnvironmentVariables(rule.Path ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(rule.Path)) continue;
            if (!seenPaths.Add(rule.Path)) continue;

            if (string.IsNullOrWhiteSpace(rule.Id))
                rule.Id = Guid.NewGuid().ToString("N");

            if (string.IsNullOrWhiteSpace(rule.Name))
                rule.Name = Path.GetFileNameWithoutExtension(rule.Path);

            if (!Enum.IsDefined(rule.TargetPlan))
                rule.TargetPlan = PlanId.Performance;

            normalized.Add(rule);
        }

        settings.Rules = normalized;
    }

    private static void NormalizeKeepAwakeSettings(KeepAwakeSettings settings)
    {
        // Keep-awake intentionally has no timeout: it remains active until the user
        // disables it from the app or tray, then Windows resumes the normal plan rules.
    }

    public void Save()
    {
        lock (_lock)
        {
            Current.AutoShutdown ??= new AutoShutdownSettings();
            Current.AutoUpdates ??= new AutoUpdateSettings();
            Current.HeavyAppDetection ??= new HeavyAppDetectionSettings();
            Current.AppPowerProfiles ??= new AppPowerProfileSettings();
            Current.KeepAwake ??= new KeepAwakeSettings();
            NormalizeScheduledPowerAction(Current.AutoShutdown);
            NormalizeAutoUpdateSettings(Current.AutoUpdates);
            NormalizeHeavyAppDetectionSettings(Current.HeavyAppDetection);
            NormalizeAppPowerProfileSettings(Current.AppPowerProfiles);
            NormalizeKeepAwakeSettings(Current.KeepAwake);
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
