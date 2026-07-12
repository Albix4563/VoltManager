using System.IO;
using System.Text.Json;
using VoltManager.Localization;
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
                    if (loaded.PowerSourcePlan == null)
                        loaded.PowerSourcePlan = new PowerSourcePlanSettings();
                    if (loaded.CpuAutomation == null)
                        loaded.CpuAutomation = new CpuAutomationSettings();
                    if (loaded.StandbyAutoCleaner == null)
                        loaded.StandbyAutoCleaner = new StandbyAutoCleanerSettings();
                    if (loaded.Widgets == null)
                        loaded.Widgets = new WidgetSettings();
                    NormalizeScheduledPowerAction(loaded.AutoShutdown);
                    NormalizeAutoUpdateSettings(loaded.AutoUpdates);
                    NormalizeHeavyAppDetectionSettings(loaded.HeavyAppDetection);
                    NormalizeAppPowerProfileSettings(loaded.AppPowerProfiles);
                    NormalizeKeepAwakeSettings(loaded.KeepAwake);
                    NormalizePowerSourcePlanSettings(loaded.PowerSourcePlan);
                    NormalizeCpuAutomationSettings(loaded.CpuAutomation);
                    NormalizeStandbyAutoCleanerSettings(loaded.StandbyAutoCleaner);
                    NormalizeWidgetSettings(loaded.Widgets);
                    NormalizeTheme(loaded);
                    NormalizeLanguage(loaded);
                    // Migrate stale repo name from pre-release installs.
                    if (loaded.UpdateRepo == "Albix4563/VoltManager")
                        loaded.UpdateRepo = "Albix4563/power_efficency";
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            // Corrupt/unreadable settings: keep a copy so user data isn't silently
            // overwritten by the next Save, then fall through to defaults.
            Logger.Error("Failed to load settings from " + _path + "; using defaults.", ex);
            BackupCorruptSettings();
        }
        return new AppSettings();
    }

    private void BackupCorruptSettings()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var backup = _path + ".corrupt";
            File.Copy(_path, backup, overwrite: true);
            Logger.Warn("Backed up unreadable settings to " + backup);
        }
        catch (Exception ex)
        {
            // Best-effort: a failed backup must not block startup.
            Logger.Warn("Could not back up corrupt settings: " + ex.Message);
        }
    }

    private static void NormalizeTheme(AppSettings settings)
    {
        settings.Theme = settings.Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => "light",
            "black" => "black",
            "auto" => "auto",
            _ => "dark",
        };
    }

    private static void NormalizeLanguage(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            // Empty = migration state; don't force a default yet.
            return;
        }
        var normalized = LanguageResolver.Normalize(settings.Language);
        if (string.IsNullOrEmpty(normalized))
        {
            Logger.Warn("Unsupported language in settings: '" + settings.Language + "'; clearing for migration.");
            settings.Language = "";
            return;
        }
        settings.Language = normalized;
    }

    private static void NormalizeScheduledPowerAction(AutoShutdownSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Time))
            settings.Time = "23:00";

        // Migrate legacy string action to enum (backwards compat).
        if (!string.IsNullOrWhiteSpace(settings.ActionLegacy))
        {
            settings.Action = NormalizePowerActionEnum(settings.ActionLegacy);
            settings.ActionLegacy = null;
        }

        // Legacy Action property used string values; normalize.
        if (!Enum.IsDefined(settings.Action))
        {
            // If Action deserialized as 0=Shutdown but was never set, try legacy value.
            settings.Action = ScheduledPowerActionType.Shutdown;
        }

        // Sanity: disable invalid relative schedules.
        if (settings.Mode == ScheduledPowerMode.Relative)
        {
            if (settings.ExecuteAtUtc == null)
            {
                settings.Mode = ScheduledPowerMode.Daily;
                settings.Enabled = false;
                settings.ExecuteAtUtc = null;
                settings.DelayMinutes = null;
                settings.CreatedAtUtc = null;
            }
        }

        if (settings.DelayMinutes.HasValue)
            settings.DelayMinutes = Math.Max(1, settings.DelayMinutes.Value);
    }

    internal static ScheduledPowerActionType NormalizePowerActionEnum(string? action) => action switch
    {
        "restart" => ScheduledPowerActionType.Restart,
        "sleep" => ScheduledPowerActionType.Sleep,
        _ => ScheduledPowerActionType.Shutdown,
    };

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

    private static void NormalizePowerSourcePlanSettings(PowerSourcePlanSettings settings)
    {
        if (!Enum.IsDefined(settings.PluggedPlan))
            settings.PluggedPlan = PlanId.Performance;

        settings.UnpluggedMode = settings.UnpluggedMode switch
        {
            "previous" => "previous",
            _ => "previous",
        };
    }

    private static void NormalizeCpuAutomationSettings(CpuAutomationSettings settings) => settings.Normalize();

    private static void NormalizeStandbyAutoCleanerSettings(StandbyAutoCleanerSettings settings)
    {
        settings.ThresholdGb = Math.Clamp(settings.ThresholdGb, 0.5, 128.0);
        settings.IntervalMinutes = Math.Clamp(settings.IntervalMinutes, 5, 1440);
    }

    private static void NormalizeWidgetSettings(WidgetSettings settings) => settings.Normalize();

    public void Save()
    {
        lock (_lock)
        {
            Current.AutoShutdown ??= new AutoShutdownSettings();
            Current.AutoUpdates ??= new AutoUpdateSettings();
            Current.HeavyAppDetection ??= new HeavyAppDetectionSettings();
            Current.AppPowerProfiles ??= new AppPowerProfileSettings();
            Current.KeepAwake ??= new KeepAwakeSettings();
            Current.PowerSourcePlan ??= new PowerSourcePlanSettings();
            Current.CpuAutomation ??= new CpuAutomationSettings();
            Current.StandbyAutoCleaner ??= new StandbyAutoCleanerSettings();
            Current.Widgets ??= new WidgetSettings();
            NormalizeScheduledPowerAction(Current.AutoShutdown);
            NormalizeAutoUpdateSettings(Current.AutoUpdates);
            NormalizeHeavyAppDetectionSettings(Current.HeavyAppDetection);
            NormalizeAppPowerProfileSettings(Current.AppPowerProfiles);
            NormalizeKeepAwakeSettings(Current.KeepAwake);
            NormalizePowerSourcePlanSettings(Current.PowerSourcePlan);
            NormalizeCpuAutomationSettings(Current.CpuAutomation);
            NormalizeStandbyAutoCleanerSettings(Current.StandbyAutoCleaner);
            NormalizeWidgetSettings(Current.Widgets);
            NormalizeTheme(Current);
            NormalizeLanguage(Current);
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOpts));
            // Atomic replace: the previous file survives intact until the move
            // completes, so a crash mid-write can never leave settings.json gone.
            File.Move(tmp, _path, overwrite: true);
        }
        // A throwing subscriber must not surface as a save failure: the file is
        // already written at this point.
        try { SettingsChanged?.Invoke(Current); }
        catch (Exception ex) { Logger.Error("SettingsChanged subscriber failed", ex); }
    }

    public void Update(AppSettings settings)
    {
        Current = settings;
        Save();
    }
}
