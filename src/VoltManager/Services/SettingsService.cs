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
    private readonly Action<string, string, string> _replaceFile;
    private readonly object _lock = new();
    private bool _needsThemeMigrationSave;
    private bool _mainKnownGood;
    private bool _backupKnownGood;
    private AppSettings _current = new();

    private string BackupPath => _path + ".bak";

    /// <summary>
    /// Returns a detached snapshot of the persisted settings state.
    /// Mutations must go through one of the Update overloads.
    /// </summary>
    public AppSettings Current
    {
        get
        {
            lock (_lock)
                return CloneSettings(_current);
        }
    }

    /// <summary>
    /// Raised after settings have been successfully persisted. Each subscriber receives
    /// its own detached snapshot; one subscriber failure does not block the others.
    /// </summary>
    public event Action<AppSettings>? SettingsChanged;

    public SettingsService(string? path = null)
        : this(path, static (source, destination, backup) => File.Replace(source, destination, backup))
    {
    }

    internal SettingsService(string? path, Action<string, string, string> replaceFile)
    {
        _path = path ?? Path.Combine(
            ValidationEnvironment.ApplicationDataRoot,
            "VoltManager", "settings.json");
        _replaceFile = replaceFile;
        _current = Load();
        if (_needsThemeMigrationSave)
            Save();
    }

    private AppSettings Load()
    {
        SettingsLoadResult main = TryLoadSettings(_path, out AppSettings? loaded, out bool needsMigration, out Exception? error);
        if (main == SettingsLoadResult.Loaded)
        {
            _mainKnownGood = true;
            _needsThemeMigrationSave = needsMigration;
            RefreshBackupFromKnownGoodMain();
            return loaded!;
        }

        // A locked file (antivirus, sync client) is not corruption: never quarantine
        // or overwrite it, it may be newer than the backup.
        bool mainUnreadable = main == SettingsLoadResult.Unreadable;
        if (main != SettingsLoadResult.Missing)
        {
            Logger.Error("Failed to load settings from " + _path + "; trying last-known-good backup.", error ?? new InvalidDataException("Invalid settings file."));
            if (!mainUnreadable)
                BackupCorruptSettings();
        }

        if (TryLoadSettings(BackupPath, out AppSettings? backup, out bool backupNeedsMigration, out _) == SettingsLoadResult.Loaded)
        {
            _backupKnownGood = true;
            Logger.Warn("Loaded settings from last-known-good backup: " + BackupPath);
            if (mainUnreadable)
                return backup!;
            _needsThemeMigrationSave = backupNeedsMigration;
            RestoreMainFromBackup();
            return backup!;
        }

        return NormalizeSettings(new AppSettings());
    }

    internal enum SettingsLoadResult { Loaded, Missing, Corrupt, Unreadable }

    private static readonly TimeSpan[] TransientReadRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(150),
        TimeSpan.FromMilliseconds(300),
    ];

    private static SettingsLoadResult TryLoadSettings(
        string path,
        out AppSettings? settings,
        out bool needsThemeMigration,
        out Exception? error)
    {
        settings = null;
        needsThemeMigration = false;
        error = null;
        if (!File.Exists(path)) return SettingsLoadResult.Missing;

        string json;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                json = File.ReadAllText(path);
                break;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return SettingsLoadResult.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= TransientReadRetryDelays.Length)
                {
                    error = ex;
                    return SettingsLoadResult.Unreadable;
                }
                Thread.Sleep(TransientReadRetryDelays[attempt]);
            }
        }

        try
        {
            needsThemeMigration = InspectThemeMigration(json);
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)
                ?? throw new JsonException("Settings payload deserialized to null.");
            settings = NormalizeSettings(settings);
            return SettingsLoadResult.Loaded;
        }
        catch (Exception ex)
        {
            error = ex;
            settings = null;
            needsThemeMigration = false;
            return SettingsLoadResult.Corrupt;
        }
    }

    private static AppSettings NormalizeSettings(AppSettings settings)
    {
        if (settings.Rules == null || settings.Rules.Count == 0 || IsOldDefaultRules(settings.Rules))
            settings.Rules = AppSettings.DefaultRules();

        settings.AutoShutdown ??= new AutoShutdownSettings();
        settings.AutoUpdates ??= new AutoUpdateSettings();
        settings.HeavyAppDetection ??= new HeavyAppDetectionSettings();
        settings.AppPowerProfiles ??= new AppPowerProfileSettings();
        settings.KeepAwake ??= new KeepAwakeSettings();
        settings.PowerSourcePlan ??= new PowerSourcePlanSettings();
        settings.GlobalHotkeys ??= new GlobalHotkeySettings();
        settings.ThermalGuard ??= new ThermalGuardSettings();
        settings.IdlePowerGuard ??= new IdlePowerGuardSettings();
        settings.CpuAutomation ??= new CpuAutomationSettings();
        settings.StandbyAutoCleaner ??= new StandbyAutoCleanerSettings();
        settings.Widgets ??= new WidgetSettings();
        settings.LanRemoteControl ??= new LanRemoteControlSettings();
        settings.Launcher ??= new LauncherSettings();
        settings.PlanGuidMap ??= new Dictionary<string, string>();
        settings.DismissedExtraPlanGuids = (settings.DismissedExtraPlanGuids ?? new List<string>())
            .Where(guid => !string.IsNullOrWhiteSpace(guid))
            .Select(guid => guid.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        NormalizeScheduledPowerAction(settings.AutoShutdown);
        NormalizeAutoUpdateSettings(settings.AutoUpdates);
        NormalizeHeavyAppDetectionSettings(settings.HeavyAppDetection);
        NormalizeAppPowerProfileSettings(settings.AppPowerProfiles);
        NormalizeKeepAwakeSettings(settings.KeepAwake);
        NormalizePowerSourcePlanSettings(settings.PowerSourcePlan);
        NormalizeGlobalHotkeySettings(settings.GlobalHotkeys);
        NormalizeThermalGuardSettings(settings.ThermalGuard);
        NormalizeIdlePowerGuardSettings(settings.IdlePowerGuard);
        NormalizeCpuAutomationSettings(settings.CpuAutomation);
        NormalizeStandbyAutoCleanerSettings(settings.StandbyAutoCleaner);
        NormalizeWidgetSettings(settings.Widgets);
        NormalizeLanRemoteControlSettings(settings.LanRemoteControl);
        settings.Launcher.Normalize();
        NormalizeThemeColor(settings);
        NormalizeLanguage(settings);
        NormalizeFont(settings);
        NormalizeAnimationLevel(settings);

        // Migrate stale repo name from pre-release installs.
        if (settings.UpdateRepo == "Albix4563/VoltManager")
            settings.UpdateRepo = "Albix4563/power_efficency";

        return settings;
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOpts);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOpts)
            ?? throw new InvalidOperationException("Could not clone application settings.");
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

    private void RefreshBackupFromKnownGoodMain()
    {
        if (!_mainKnownGood || !File.Exists(_path)) return;
        if (TryCopyAtomically(_path, BackupPath))
            _backupKnownGood = true;
    }

    private void RestoreMainFromBackup()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string restore = _path + ".restore.tmp";
            File.Copy(BackupPath, restore, overwrite: true);
            File.Move(restore, _path, overwrite: true);
            _mainKnownGood = true;
        }
        catch (Exception ex)
        {
            _mainKnownGood = false;
            Logger.Warn("Could not restore settings from backup: " + ex.Message);
        }
    }

    private static bool InspectThemeMigration(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return true;

        var root = document.RootElement;
        bool hasLegacyTheme = root.TryGetProperty("theme", out _);
        bool hasThemeColor = root.TryGetProperty("themeColor", out var themeColorElement);
        bool validThemeColor = hasThemeColor
            && themeColorElement.ValueKind == JsonValueKind.String
            && AppThemeColorExtensions.TryParse(themeColorElement.GetString(), out _);

        return hasLegacyTheme || !validThemeColor;
    }

    private static void NormalizeThemeColor(AppSettings settings)
    {
        settings.ThemeColor = settings.ThemeColor.Normalize();
        settings.CustomThemeColor = ThemeService.TryNormalizeCustomColor(settings.CustomThemeColor, out string normalized)
            ? normalized
            : null;
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

    private static void NormalizeAnimationLevel(AppSettings settings)
    {
        settings.AnimationLevel = settings.AnimationLevel?.Trim().ToLowerInvariant() switch
        {
            "low" => "low",
            "medium" => "medium",
            "high" => "high",
            "auto" => "auto",
            _ => "auto",
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
        settings.PriorityApplicationPaths = NormalizeExecutablePathList(settings.PriorityApplicationPaths);
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

    private static List<string> NormalizeExecutablePathList(List<string>? paths)
    {
        var normalized = new List<string>();
        if (paths == null) return normalized;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? entry in paths)
        {
            string value = Environment.ExpandEnvironmentVariables(entry ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) continue;
            try { value = Path.GetFullPath(value); }
            catch { continue; }
            if (!string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static void NormalizeLanRemoteControlSettings(LanRemoteControlSettings settings)
    {
        if (settings.Port is < 1 or > 65535)
            settings.Port = 51737;
    }

    public void Save()
    {
        AppSettings notification;
        lock (_lock)
        {
            var next = NormalizeSettings(CloneSettings(_current));
            Persist(next);
            _current = next;
            notification = CloneSettings(next);
        }

        NotifySettingsChanged(notification);
    }

    private void Persist(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        bool hadKnownGoodMain = _mainKnownGood && File.Exists(_path);
        if (hadKnownGoodMain)
        {
            try
            {
                _replaceFile(tmp, _path, BackupPath);
                _mainKnownGood = true;
                _backupKnownGood = true;
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn("Atomic settings replace unavailable; using copy/move fallback: " + ex.Message);
                if (TryCopyAtomically(_path, BackupPath))
                    _backupKnownGood = true;
            }
        }

        File.Move(tmp, _path, overwrite: true);
        _mainKnownGood = true;
        if (!_backupKnownGood && TryCopyAtomically(_path, BackupPath))
            _backupKnownGood = true;
    }

    private static bool TryCopyAtomically(string source, string destination)
    {
        string temporary = destination + ".tmp";
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            Logger.Warn("Could not refresh settings backup: " + ex.Message);
            return false;
        }
    }

    private void NotifySettingsChanged(AppSettings snapshot)
    {
        Delegate[] subscribers = SettingsChanged?.GetInvocationList() ?? [];
        foreach (Action<AppSettings> subscriber in subscribers.Cast<Action<AppSettings>>())
        {
            try
            {
                subscriber(CloneSettings(snapshot));
            }
            catch (Exception ex)
            {
                Logger.Error("SettingsChanged subscriber failed", ex);
            }
        }
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
        ArgumentNullException.ThrowIfNull(settings);

        AppSettings notification;
        lock (_lock)
        {
            var next = NormalizeSettings(CloneSettings(settings));
            Persist(next);
            _current = next;
            notification = CloneSettings(next);
        }

        NotifySettingsChanged(notification);
    }

    public void Update(Action<AppSettings> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        AppSettings notification;
        lock (_lock)
        {
            var next = CloneSettings(_current);
            mutation(next);
            NormalizeSettings(next);
            Persist(next);
            _current = next;
            notification = CloneSettings(next);
        }

        NotifySettingsChanged(notification);
    }
}
