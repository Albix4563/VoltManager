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
    private bool _needsThemeMigrationSave;

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager", "settings.json");
        Current = Load();
        if (_needsThemeMigrationSave)
            Save();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                InspectThemeMigration(json);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null)
                {
                    if (loaded.Rules == null || loaded.Rules.Count == 0)
                        loaded.Rules = AppSettings.DefaultRules();
                    else if (IsOldDefaultRules(loaded.Rules))
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
                    if (loaded.ThermalGuard == null)
                        loaded.ThermalGuard = new ThermalGuardSettings();
                    if (loaded.IdlePowerGuard == null)
                        loaded.IdlePowerGuard = new IdlePowerGuardSettings();
                    if (loaded.CpuAutomation == null)
                        loaded.CpuAutomation = new CpuAutomationSettings();
                    if (loaded.GlobalHotkeys == null)
                        loaded.GlobalHotkeys = new GlobalHotkeySettings();
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
                    NormalizeGlobalHotkeySettings(loaded.GlobalHotkeys);
                    NormalizeThermalGuardSettings(loaded.ThermalGuard);
                    NormalizeIdlePowerGuardSettings(loaded.IdlePowerGuard);
                    NormalizeCpuAutomationSettings(loaded.CpuAutomation);
                    NormalizeStandbyAutoCleanerSettings(loaded.StandbyAutoCleaner);
                    NormalizeWidgetSettings(loaded.Widgets);
                    NormalizeThemeColor(loaded);
                    NormalizeLanguage(loaded);
                    NormalizeFont(loaded);
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

    private void InspectThemeMigration(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            _needsThemeMigrationSave = true;
            return;
        }

        var root = document.RootElement;
        bool hasLegacyTheme = root.TryGetProperty("theme", out _);
        bool hasThemeColor = root.TryGetProperty("themeColor", out var themeColorElement);
        bool validThemeColor = hasThemeColor
            && themeColorElement.ValueKind == JsonValueKind.String
            && AppThemeColorExtensions.TryParse(themeColorElement.GetString(), out _);

        _needsThemeMigrationSave = hasLegacyTheme || !validThemeColor;
    }

    private static void NormalizeThemeColor(AppSettings settings)
    {
        settings.ThemeColor = settings.ThemeColor.Normalize();
    }

    private static void NormalizeFont(AppSettings settings)
    {
        settings.Font = settings.Font?.Trim().ToLowerInvariant() switch
        {
            "segoe-ui" => "segoe-ui",
            "arial" => "arial",
            "calibri" => "calibri",
            "verdana" => "verdana",
            "tahoma" => "tahoma",
            "trebuchet-ms" => "trebuchet-ms",
            "georgia" => "georgia",
            "times-new-roman" => "times-new-roman",
            "consolas" => "consolas",
            _ => "inter",
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

    public static void NormalizeHeavyAppDetectionSettings(HeavyAppDetectionSettings settings)
    {
        settings.MinWorkingSetMb = Math.Clamp(settings.MinWorkingSetMb, 256, 8192);

        if (!settings.UseWindowsGpuPreferences && !settings.UseGameInstallHeuristics && !settings.UseResourceHeuristics)
            settings.UseWindowsGpuPreferences = true;

        settings.AlwaysGamePaths = NormalizeUserPathList(settings.AlwaysGamePaths);
        settings.NeverGamePaths = NormalizeUserPathList(settings.NeverGamePaths);
    }

    // Hand-edited lists: drop blanks, dedupe case-insensitively, and cap so a runaway
    // config cannot turn every classification into a linear scan of thousands of entries.
    private const int MaxUserPathEntries = 200;

    private static List<string> NormalizeUserPathList(List<string>? paths)
    {
        var normalized = new List<string>();
        if (paths == null) return normalized;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? entry in paths)
        {
            string value = Environment.ExpandEnvironmentVariables(entry ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (!seen.Add(value)) continue;

            normalized.Add(value);
            if (normalized.Count >= MaxUserPathEntries) break;
        }

        return normalized;
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
        // Optional safety caps (battery auto-off + max duration) live on KeepAwakeSettings.
        settings.Normalize();
    }

    private static void NormalizePowerSourcePlanSettings(PowerSourcePlanSettings settings)
    {
        if (!Enum.IsDefined(settings.PluggedPlan))
            settings.PluggedPlan = PlanId.Performance;

        settings.LowBatteryThresholdPercent = Math.Clamp(settings.LowBatteryThresholdPercent, 5, 50);

        settings.UnpluggedMode = settings.UnpluggedMode switch
        {
            "previous" => "previous",
            _ => "previous",
        };
    }

    private static void NormalizeGlobalHotkeySettings(GlobalHotkeySettings settings)
    {
        settings.PowerSaver = NormalizeHotkey(settings.PowerSaver, "Ctrl+Alt+1");
        settings.Balanced = NormalizeHotkey(settings.Balanced, "Ctrl+Alt+2");
        settings.Performance = NormalizeHotkey(settings.Performance, "Ctrl+Alt+3");
        settings.Auto = NormalizeHotkey(settings.Auto, "Ctrl+Alt+0");
        settings.KeepAwakeToggle = NormalizeHotkey(settings.KeepAwakeToggle, "Ctrl+Alt+K");
    }

    private static string NormalizeHotkey(string? value, string fallback)
    {
        value = value?.Trim();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static void NormalizeThermalGuardSettings(ThermalGuardSettings settings) => settings.Normalize();

    private static void NormalizeIdlePowerGuardSettings(IdlePowerGuardSettings settings) => settings.Normalize();

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
            Current.GlobalHotkeys ??= new GlobalHotkeySettings();
            Current.ThermalGuard ??= new ThermalGuardSettings();
            Current.IdlePowerGuard ??= new IdlePowerGuardSettings();
            Current.CpuAutomation ??= new CpuAutomationSettings();
            Current.StandbyAutoCleaner ??= new StandbyAutoCleanerSettings();
            Current.Widgets ??= new WidgetSettings();
            NormalizeScheduledPowerAction(Current.AutoShutdown);
            NormalizeAutoUpdateSettings(Current.AutoUpdates);
            NormalizeHeavyAppDetectionSettings(Current.HeavyAppDetection);
            NormalizeAppPowerProfileSettings(Current.AppPowerProfiles);
            NormalizeKeepAwakeSettings(Current.KeepAwake);
            NormalizePowerSourcePlanSettings(Current.PowerSourcePlan);
            NormalizeGlobalHotkeySettings(Current.GlobalHotkeys);
            NormalizeThermalGuardSettings(Current.ThermalGuard);
            NormalizeIdlePowerGuardSettings(Current.IdlePowerGuard);
            NormalizeCpuAutomationSettings(Current.CpuAutomation);
            NormalizeStandbyAutoCleanerSettings(Current.StandbyAutoCleaner);
            NormalizeWidgetSettings(Current.Widgets);
            NormalizeThemeColor(Current);
            NormalizeLanguage(Current);
            NormalizeFont(Current);
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

    private static bool IsOldDefaultRules(List<AutomationRule>? rules)
    {
        if (rules == null || rules.Count != 3) return false;

        AutomationRule? saver = null;
        AutomationRule? balanced = null;
        AutomationRule? performance = null;

        foreach (var r in rules)
        {
            if (r.Id == "saver") saver = r;
            else if (r.Id == "balanced") balanced = r;
            else if (r.Id == "performance") performance = r;
        }

        if (saver == null || !saver.Enabled || saver.Comparison != "lt" || saver.ThresholdPct != 10 || saver.DurationMinutes != 1 || saver.TargetPlan != PlanId.PowerSaver)
            return false;

        if (balanced == null || !balanced.Enabled || balanced.Comparison != "gt" || balanced.ThresholdPct != 10 || balanced.DurationMinutes != 1 || balanced.TargetPlan != PlanId.Balanced)
            return false;

        if (performance == null || !performance.Enabled || performance.Comparison != "gt" || performance.ThresholdPct != 50 || performance.DurationMinutes != 1 || performance.TargetPlan != PlanId.Performance)
            return false;

        return true;
    }

    public void Update(AppSettings settings)
    {
        Current = settings;
        Save();
    }
}
