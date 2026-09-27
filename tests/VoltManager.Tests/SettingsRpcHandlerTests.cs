using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class SettingsRpcHandlerTests
{
    private sealed class FakeDialogs : IBridgeFileDialogService
    {
        public string? SavePath { get; set; }
        public string? OpenPath { get; set; }
        public Task<string?> SaveFileAsync(BridgeSaveFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SavePath);
        public Task<string?> OpenFileAsync(BridgeOpenFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult(OpenPath);
    }

    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    private static SettingsRpcHandler CreateHandler(
        SettingsService settings,
        FakeDialogs? dialogs = null,
        Action? refreshProfiles = null,
        Action? refreshHeavy = null,
        Func<bool, bool>? setStartup = null)
    {
        var loc = new LocalizationService();
        var actions = new SettingsRpcActions(
            GetWebTheme: () => new { color = "blue" },
            GetWebThemeCatalog: () => new[] { "blue" },
            RefreshAppPowerProfiles: refreshProfiles ?? (() => { }),
            RefreshHeavyAppDetection: refreshHeavy ?? (() => { }),
            RebuildJumpList: () => { },
            IsStartWithWindowsEnabled: () => false,
            SetStartWithWindows: setStartup ?? (enabled => enabled),
            UtcNow: () => new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
        return new SettingsRpcHandler(settings, loc, actions, dialogs ?? new FakeDialogs());
    }

    [Fact]
    public void Methods_match_settings_contract()
    {
        var handler = CreateHandler(TestSettings.Create());
        string[] expected =
        [
            "getSettings", "previewThemeColor", "setThemeColor", "saveSettings", "setLanguage",
            "setStartWithWindows", "setCloseToTray", "setAutoUpdateChecks",
            "setSilentAutoUpdates", "setUpdateChannel", "snoozeUpdate",
            "skipUpdateVersion", "getStandbyAutoCleanSettings",
            "setStandbyAutoCleanSettings", "exportSettings", "importSettings",
        ];

        Assert.Equal(expected.OrderBy(x => x), handler.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task CustomTheme_preview_is_non_persistent_and_apply_persists_normalized_color()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(state => state.ThemeColor = AppThemeColor.Red);
        var handler = CreateHandler(settings);

        object? preview = await handler.HandleAsync(
            "previewThemeColor",
            Payload(new { themeColor = "custom", customColor = "#F008" }),
            CancellationToken.None);

        Assert.Null(settings.Current.CustomThemeColor);
        using (var previewDoc = JsonDocument.Parse(JsonSerializer.Serialize(preview, BridgeRpc.JsonOpts)))
        {
            Assert.Equal("custom", previewDoc.RootElement.GetProperty("themeColor").GetString());
            Assert.Equal("#8D080F", previewDoc.RootElement.GetProperty("customColor").GetString());
        }

        object? applied = await handler.HandleAsync(
            "setThemeColor",
            Payload(new { themeColor = "custom", customColor = "#F008" }),
            CancellationToken.None);

        Assert.Equal(AppThemeColor.Red, settings.Current.ThemeColor);
        Assert.Equal("#8D080F", settings.Current.CustomThemeColor);
        using var appliedDoc = JsonDocument.Parse(JsonSerializer.Serialize(applied, BridgeRpc.JsonOpts));
        Assert.Equal("custom", appliedDoc.RootElement.GetProperty("themeColor").GetString());
        Assert.Equal("#8D080F", appliedDoc.RootElement.GetProperty("customColor").GetString());
    }

    [Fact]
    public async Task CustomTheme_rejects_invalid_hex()
    {
        var handler = CreateHandler(TestSettings.Create());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync(
                "setThemeColor",
                Payload(new { themeColor = "custom", customColor = "#12" }),
                CancellationToken.None));
    }

    [Fact]
    public async Task SaveSettings_preserves_runtime_owned_state_and_refreshes_profiles()
    {
        SettingsService settings = TestSettings.Create();
        settings.Update(s =>
        {
            s.PlanGuidMap["Balanced"] = "machine-guid";
            s.DismissedExtraPlanGuids.Add("dismissed-guid");
            s.AutostartTaskSchemaVersion = 7;
            s.Language = "it";
            s.Widgets.Enabled = false;
            s.AutoUpdates.SnoozedUntilUtc = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
            s.Launcher.CustomApps.Add(new CustomLauncherApp { Path = @"D:\Games\tool.exe" });
            s.Launcher.HiddenIds.Add("steam");
        });
        int profiles = 0, heavy = 0;
        var handler = CreateHandler(settings, refreshProfiles: () => profiles++, refreshHeavy: () => heavy++);
        var incoming = new AppSettings
        {
            CloseToTray = false,
            Language = "",
            PlanGuidMap = new Dictionary<string, string> { ["Balanced"] = "foreign-guid" },
            AutostartTaskSchemaVersion = 0,
            Launcher = new LauncherSettings
            {
                CustomApps = [new CustomLauncherApp { Path = @"C:\Users\x\Downloads\evil.exe" }],
            },
        };

        object? result = await handler.HandleAsync("saveSettings", Payload(incoming), CancellationToken.None);

        Assert.False(settings.Current.CloseToTray);
        Assert.Equal("machine-guid", settings.Current.PlanGuidMap["Balanced"]);
        Assert.Equal(new[] { "dismissed-guid" }, settings.Current.DismissedExtraPlanGuids);
        Assert.Equal(7, settings.Current.AutostartTaskSchemaVersion);
        Assert.Equal("it", settings.Current.Language);
        Assert.False(settings.Current.Widgets.Enabled);
        Assert.Equal(@"D:\Games\tool.exe", Assert.Single(settings.Current.Launcher.CustomApps).Path);
        Assert.Equal(new[] { "steam" }, settings.Current.Launcher.HiddenIds);
        Assert.Equal(new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc),
            settings.Current.AutoUpdates.SnoozedUntilUtc);
        Assert.Equal(1, profiles);
        Assert.Equal(1, heavy);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task SetStartWithWindows_uses_injected_action_and_persists_effective_state()
    {
        SettingsService settings = TestSettings.Create();
        bool? requested = null;
        var handler = CreateHandler(settings, setStartup: enabled =>
        {
            requested = enabled;
            return false;
        });

        await handler.HandleAsync("setStartWithWindows", Payload(new { enabled = true }), CancellationToken.None);

        Assert.True(requested);
        Assert.False(settings.Current.StartWithWindows);
    }

    [Fact]
    public async Task SkipUpdateVersion_rejects_empty_version()
    {
        var handler = CreateHandler(TestSettings.Create());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync("skipUpdateVersion", Payload(new { version = "  v  " }), CancellationToken.None));
    }

    [Fact]
    public async Task ExportSettings_cancelled_keeps_filesystem_untouched()
    {
        var handler = CreateHandler(TestSettings.Create(), new FakeDialogs { SavePath = null });
        object? result = await handler.HandleAsync("exportSettings", default, CancellationToken.None);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
    }
}

