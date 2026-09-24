using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Bridge.Handlers;

public sealed record SettingsRpcActions(
    Func<object> GetWebTheme,
    Func<object> GetWebThemeCatalog,
    Action RefreshAppPowerProfiles,
    Action RefreshHeavyAppDetection,
    Action RebuildJumpList,
    Func<bool> IsStartWithWindowsEnabled,
    Func<bool, bool> SetStartWithWindows,
    Func<DateTime> UtcNow);

public sealed class SettingsRpcHandler : IBridgeRpcHandler
{
    private static readonly JsonSerializerOptions BackupJsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] RegisteredMethods =
    [
        "getSettings", "setThemeColor", "saveSettings", "setLanguage",
        "setStartWithWindows", "setCloseToTray", "setAutoUpdateChecks",
        "setSilentAutoUpdates", "setUpdateChannel", "snoozeUpdate",
        "skipUpdateVersion", "getStandbyAutoCleanSettings",
        "setStandbyAutoCleanSettings", "exportSettings", "importSettings",
    ];

    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly SettingsRpcActions _actions;
    private readonly IBridgeFileDialogService _dialogs;

    public SettingsRpcHandler(
        SettingsService settings,
        LocalizationService loc,
        SettingsRpcActions actions,
        IBridgeFileDialogService dialogs)
    {
        _settings = settings;
        _loc = loc;
        _actions = actions;
        _dialogs = dialogs;
    }

    public static IReadOnlyCollection<string> MethodNames => RegisteredMethods;
    public IReadOnlyCollection<string> Methods => MethodNames;

    public async Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken cancellationToken)
    {
        switch (method)
        {
            case "getSettings":
                return new
                {
                    settings = _settings.Current,
                    startWithWindows = _actions.IsStartWithWindowsEnabled(),
                    theme = _actions.GetWebTheme(),
                    themeCatalog = _actions.GetWebThemeCatalog(),
                    resolvedLanguage = _loc.CurrentLanguage,
                    resolvedLocale = _loc.CurrentCulture.Name,
                };

            case "setThemeColor":
            {
                string? requested = payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("themeColor", out JsonElement themeColorElement)
                    ? themeColorElement.GetString()
                    : null;
                AppThemeColorExtensions.TryParse(requested, out AppThemeColor themeColor);
                _settings.Update(state => state.ThemeColor = themeColor);
                return _actions.GetWebTheme();
            }

            case "saveSettings":
            {
                AppSettings settings = BridgePayload.Deserialize<AppSettings>(
                    payload, _loc.T("Error_InvalidSettings"));
                PreserveRuntimeOwnedSettings(settings, _settings.Current);
                _settings.Update(settings);
                _actions.RefreshAppPowerProfiles();
                _actions.RefreshHeavyAppDetection();
                return new { success = true };
            }

            case "setLanguage":
            {
                string lang = BridgePayload.RequiredString(
                    payload, "language", _loc.T("Error_UnknownMethod", ""));
                if (!LanguageResolver.IsSupported(lang))
                    throw new ArgumentException(_loc.T("Error_UnknownMethod", lang));
                string normalized = LanguageResolver.Normalize(lang);
                _settings.Update(state => state.Language = normalized);
                _loc.SetLanguage(normalized);
                try { _actions.RebuildJumpList(); } catch { }
                return new { success = true, language = normalized, locale = _loc.CurrentCulture.Name };
            }

            case "setStartWithWindows":
            {
                bool enable = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                bool okStart = await Task.Run(() => _actions.SetStartWithWindows(enable), cancellationToken);
                _settings.Update(state => state.StartWithWindows = enable && okStart);
                return new { success = okStart };
            }

            case "setCloseToTray":
            {
                bool enabled = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                _settings.Update(state => state.CloseToTray = enabled);
                return new { success = true };
            }

            case "setAutoUpdateChecks":
            {
                bool enable = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                _settings.Update(state =>
                {
                    state.AutoUpdates.Enabled = enable;
                    if (enable)
                        state.AutoUpdates.SnoozedUntilUtc = null;
                });
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "setSilentAutoUpdates":
            {
                bool enable = BridgePayload.RequiredBoolean(payload, "enabled", "Missing enabled");
                _settings.Update(state => state.AutoUpdates.SilentInstallEnabled = enable);
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "setUpdateChannel":
            {
                string channel = BridgePayload.RequiredString(payload, "channel", "Missing update channel");
                _settings.Update(state => state.AutoUpdates.UpdateChannel = channel);
                return new { success = true, autoUpdates = _settings.Current.AutoUpdates };
            }

            case "snoozeUpdate":
            {
                int minutes = BridgePayload.OptionalInt32(payload, "minutes", 30);
                minutes = UpdateSchedulePolicy.NormalizeSnoozeMinutes(minutes);
                DateTime snoozedUntilUtc = _actions.UtcNow().AddMinutes(minutes);
                _settings.Update(state => state.AutoUpdates.SnoozedUntilUtc = snoozedUntilUtc);
                return new { success = true, snoozedUntilUtc = _settings.Current.AutoUpdates.SnoozedUntilUtc };
            }

            case "skipUpdateVersion":
            {
                string version = BridgePayload.RequiredString(
                    payload, "version", _loc.T("Error_MissingUpdateVersion"))
                    .Trim().TrimStart('v', 'V').Trim();
                if (version.Length == 0)
                    throw new ArgumentException(_loc.T("Error_MissingUpdateVersion"));
                _settings.Update(state =>
                {
                    state.AutoUpdates.SkippedVersion = version;
                    state.AutoUpdates.SnoozedUntilUtc = null;
                });
                return new { success = true, skippedVersion = version };
            }

            case "getStandbyAutoCleanSettings":
                return _settings.Current.StandbyAutoCleaner;

            case "setStandbyAutoCleanSettings":
            {
                StandbyAutoCleanerSettings autoSettings = BridgePayload.Deserialize<StandbyAutoCleanerSettings>(
                    payload, "Impostazioni StandbyAutoCleaner non valide");
                StandbyAutoCleanerSettings savedSettings = SaveStandbyAutoCleanSettings(_settings, autoSettings);
                return new { success = true, settings = savedSettings };
            }

            case "exportSettings":
                return await ExportSettingsAsync(cancellationToken);

            case "importSettings":
                return await ImportSettingsAsync(cancellationToken);

            default:
                throw new ArgumentException($"Handler cannot process RPC method '{method}'.");
        }
    }

    private async Task<object> ExportSettingsAsync(CancellationToken cancellationToken)
    {
        DateTime now = _actions.UtcNow().ToLocalTime();
        string? path = await _dialogs.SaveFileAsync(
            new BridgeSaveFileRequest(
                _loc.T("FilePicker_ExportTitle"),
                _loc.T("FilePicker_JsonFilter"),
                $"voltmanager-settings-{now:yyyyMMdd}.json"),
            cancellationToken);
        if (path == null)
            return new { success = false, cancelled = true };

        string json = JsonSerializer.Serialize(_settings.Current, BackupJsonOpts);
        await File.WriteAllTextAsync(path, json, cancellationToken);
        return new { success = true, path };
    }

    private async Task<object> ImportSettingsAsync(CancellationToken cancellationToken)
    {
        string? path = await _dialogs.OpenFileAsync(
            new BridgeOpenFileRequest(
                _loc.T("FilePicker_ImportTitle"),
                _loc.T("FilePicker_JsonFilter")),
            cancellationToken);
        if (path == null)
            return new { success = false, cancelled = true };

        string json = await File.ReadAllTextAsync(path, cancellationToken);
        AppSettings imported = JsonSerializer.Deserialize<AppSettings>(json, BackupJsonOpts)
            ?? throw new ArgumentException(_loc.T("Error_InvalidBackupFile"));
        PreserveRuntimeOwnedSettings(imported, _settings.Current);
        _settings.Update(imported);
        _actions.RefreshAppPowerProfiles();
        _actions.RefreshHeavyAppDetection();
        return new { success = true, path };
    }

    internal static void PreserveRuntimeOwnedSettings(AppSettings settings, AppSettings current)
    {
        settings.PlanGuidMap = current.PlanGuidMap;
        settings.Override = current.Override;
        settings.AutostartTaskSchemaVersion = current.AutostartTaskSchemaVersion;
        settings.StandbyAutoCleaner = current.StandbyAutoCleaner;
        settings.LanRemoteControl = current.LanRemoteControl;
        settings.AutoShutdown ??= new AutoShutdownSettings();
        settings.AutoUpdates ??= new AutoUpdateSettings();
        settings.HeavyAppDetection ??= new HeavyAppDetectionSettings();
        settings.AppPowerProfiles ??= new AppPowerProfileSettings();
        settings.CpuAutomation ??= new CpuAutomationSettings();
        current.AutoShutdown ??= new AutoShutdownSettings();
        current.AutoUpdates ??= new AutoUpdateSettings();
        current.Widgets ??= new WidgetSettings();
        current.Widgets.Normalize();
        settings.Widgets = current.Widgets;
        settings.AutoShutdown = current.AutoShutdown;
        settings.AutoUpdates.SnoozedUntilUtc = current.AutoUpdates.SnoozedUntilUtc;
        settings.AutoUpdates.SkippedVersion = current.AutoUpdates.SkippedVersion;
        if (string.IsNullOrWhiteSpace(settings.Language) || !LanguageResolver.IsSupported(settings.Language))
            settings.Language = current.Language ?? "";
        else
            settings.Language = LanguageResolver.Normalize(settings.Language);
    }

    internal static StandbyAutoCleanerSettings SaveStandbyAutoCleanSettings(
        SettingsService settingsService,
        StandbyAutoCleanerSettings autoSettings)
    {
        settingsService.Update(state => state.StandbyAutoCleaner = autoSettings);
        return settingsService.Current.StandbyAutoCleaner;
    }
}
