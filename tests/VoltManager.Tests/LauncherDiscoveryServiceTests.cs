using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class LauncherDiscoveryServiceTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"voltmanager-launcher-{Guid.NewGuid():N}.json");
    private readonly FakeLauncherEnvironment _env = new();
    private readonly List<(string Path, string? Args)> _launched = new();

    public void Dispose()
    {
        try { File.Delete(_settingsPath); } catch { }
    }

    private LauncherDiscoveryService Create(SettingsService? settings = null)
        => new(
            settings ?? new SettingsService(_settingsPath),
            _env,
            path => "data:image/png;base64,AA==",
            (path, args) => _launched.Add((path, args)),
            LauncherDiscoveryService.DefaultCatalog);

    [Fact]
    public async Task Detects_steam_from_registry_and_epic_from_default_path()
    {
        _env.Registry[(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamExe")] = "c:/program files (x86)/steam/steam.exe";
        _env.Files.Add(@"C:\Program Files (x86)\Steam\steam.exe");
        _env.Files.Add(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe");

        var launchers = await Create().GetLaunchersAsync(refresh: false);

        Assert.Equal(new[] { "steam", "epic" }, launchers.Select(l => l.Id));
        Assert.All(launchers, l => Assert.Equal("detected", l.Source));
        Assert.Equal(@"c:\program files (x86)\steam\steam.exe", launchers[0].Path, ignoreCase: true);
        Assert.NotNull(launchers[0].IconDataUrl);
    }

    [Fact]
    public async Task Registry_path_that_no_longer_exists_is_skipped()
    {
        _env.Registry[(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamExe")] = @"D:\Old\steam.exe";

        var launchers = await Create().GetLaunchersAsync(refresh: false);

        Assert.Empty(launchers);
    }

    [Fact]
    public async Task Detects_from_uninstall_entry_and_riot_installs_json()
    {
        _env.Uninstall.Add(new UninstallEntry("Battle.net", @"E:\Games\Battle.net", null));
        _env.Files.Add(@"E:\Games\Battle.net\Battle.net Launcher.exe");
        _env.Texts[@"C:\ProgramData\Riot Games\RiotClientInstalls.json"] = "{\"rc_default\":\"E:/Riot Games/Riot Client/RiotClientServices.exe\"}";
        _env.Files.Add(@"E:\Riot Games\Riot Client\RiotClientServices.exe");

        var launchers = await Create().GetLaunchersAsync(refresh: false);

        Assert.Equal(new[] { "battlenet", "riot" }, launchers.Select(l => l.Id));
        Assert.Equal(@"E:\Riot Games\Riot Client\RiotClientServices.exe", launchers[1].Path);
    }

    [Fact]
    public async Task Launch_rejects_unknown_ids_and_starts_known_ones()
    {
        _env.Files.Add(@"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe");
        var service = Create();

        var unknown = await service.LaunchAsync(@"C:\Windows\System32\cmd.exe");
        var known = await service.LaunchAsync("gog");

        Assert.False(unknown.Success);
        Assert.Equal("unknown", unknown.Error);
        Assert.True(known.Success);
        Assert.Equal(@"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe", Assert.Single(_launched).Path);
    }

    [Fact]
    public async Task Custom_apps_can_be_added_hidden_launched_and_removed()
    {
        const string exe = @"D:\Tools\MyApp.exe";
        _env.Files.Add(exe);
        var settings = new SettingsService(_settingsPath);
        var service = Create(settings);
        int changes = 0;
        service.Changed += () => changes++;

        var added = service.AddCustom(exe, null, "games");
        Assert.StartsWith("custom:", added.Id);
        Assert.Equal("MyApp", added.Name);
        Assert.Equal("games", added.Category);

        Assert.True(service.SetHidden(added.Id, true));
        var entry = Assert.Single(await service.GetLaunchersAsync(false));
        Assert.True(entry.Hidden);

        Assert.True((await service.LaunchAsync(added.Id)).Success);
        Assert.Equal(exe, Assert.Single(_launched).Path);

        Assert.True(service.RemoveCustom(added.Id));
        Assert.Empty(await service.GetLaunchersAsync(false));
        Assert.Empty(new SettingsService(_settingsPath).Current.Launcher.HiddenIds);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void AddCustom_rejects_missing_files_and_unsupported_extensions()
    {
        var service = Create();
        _env.Files.Add(@"D:\Docs\notes.txt");

        Assert.Throws<FileNotFoundException>(() => service.AddCustom(@"D:\Nope\app.exe", null, "games"));
        Assert.Throws<ArgumentException>(() => service.AddCustom(@"D:\Docs\notes.txt", null, "games"));
    }

    [Theory]
    [InlineData(@"D:\Tools\run.bat")]
    [InlineData(@"D:\Tools\run.cmd")]
    [InlineData(@"\\server\share\app.exe")]
    public void AddCustom_rejects_scripts_and_network_paths(string path)
    {
        var service = Create();
        _env.Files.Add(path);

        Assert.Throws<ArgumentException>(() => service.AddCustom(path, null, "games"));
    }

    [Fact]
    public void SetHidden_ignores_unknown_ids()
    {
        Assert.False(Create().SetHidden("not-a-launcher", true));
    }

    [Fact]
    public async Task Missing_custom_app_is_reported_unavailable_and_not_launched()
    {
        const string exe = @"D:\Tools\Gone.exe";
        _env.Files.Add(exe);
        var service = Create();
        var added = service.AddCustom(exe, "Gone", "apps");
        _env.Files.Remove(exe);

        var entry = Assert.Single(await service.GetLaunchersAsync(false));
        var result = await service.LaunchAsync(added.Id);

        Assert.False(entry.Available);
        Assert.False(result.Success);
        Assert.Equal("missing", result.Error);
        Assert.Empty(_launched);
    }

    [Fact]
    public void LauncherSettings_Normalize_dedupes_and_recomputes_ids()
    {
        var settings = new LauncherSettings
        {
            CustomApps =
            [
                new CustomLauncherApp { Id = "forged", Name = " ", Path = @" D:\A\app.exe " },
                new CustomLauncherApp { Id = "x", Name = "Dup", Path = @"d:\a\APP.exe" },
                new CustomLauncherApp { Path = "" },
                new CustomLauncherApp { Path = @"D:\Tools\run.bat" },
                new CustomLauncherApp { Path = @"\\server\share\app.exe" },
                new CustomLauncherApp { Path = "relative.exe" },
            ],
            HiddenIds = ["steam", " steam ", "", "STEAM"],
        };

        settings.Normalize();

        var app = Assert.Single(settings.CustomApps);
        Assert.Equal(LauncherSettings.CustomIdFor(@"D:\A\app.exe"), app.Id);
        Assert.Equal("app", app.Name);
        Assert.Equal("games", app.Category);
        Assert.Equal(new[] { "steam" }, settings.HiddenIds);
    }

    [Fact]
    public void LauncherSettings_Normalize_defaults_unknown_categories_to_games()
    {
        var settings = new LauncherSettings
        {
            CustomApps =
            [
                new CustomLauncherApp { Path = @"D:\A\one.exe" },
                new CustomLauncherApp { Path = @"D:\A\two.exe", Category = "other" },
                new CustomLauncherApp { Path = @"D:\A\three.exe", Category = "APPS" },
            ],
        };

        settings.Normalize();

        Assert.Equal(new[] { "games", "games", "apps" }, settings.CustomApps.Select(x => x.Category));
    }

    [Fact]
    public void AddCustom_enforces_limit_per_category()
    {
        var settings = new SettingsService(_settingsPath);
        var service = Create(settings);
        for (int i = 0; i <= LauncherSettings.MaxPerCategory; i++)
            _env.Files.Add($@"D:\Tools\Game{i}.exe");
        _env.Files.Add(@"D:\Tools\App.exe");

        for (int i = 0; i < LauncherSettings.MaxPerCategory; i++)
            service.AddCustom($@"D:\Tools\Game{i}.exe", null, "games");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.AddCustom($@"D:\Tools\Game{LauncherSettings.MaxPerCategory}.exe", null, "games"));
        Assert.Equal(LauncherDiscoveryService.LimitReachedMessage, ex.Message);

        var appsEntry = service.AddCustom(@"D:\Tools\App.exe", null, "apps");
        Assert.Equal("apps", appsEntry.Category);
    }

    [Fact]
    public async Task Entries_carry_category_and_detected_launchers_are_games()
    {
        _env.Registry[(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamExe")] = @"C:\Steam\steam.exe";
        _env.Files.Add(@"C:\Steam\steam.exe");
        _env.Files.Add(@"D:\Tools\Tool.exe");
        var service = Create();
        service.AddCustom(@"D:\Tools\Tool.exe", "Tool", "apps");

        var entries = await service.GetLaunchersAsync(false);

        Assert.Equal("games", entries.Single(x => x.Source == "detected").Category);
        Assert.Equal("apps", entries.Single(x => x.Source == "custom").Category);
    }

    [Fact]
    public async Task RpcHandler_routes_launch_and_hidden_calls()
    {
        string? launchedId = null;
        var handler = new LauncherRpcHandler(new LauncherRpcActions(
            (_, _) => Task.FromResult<IReadOnlyList<LauncherEntry>>([]),
            (id, _) => { launchedId = id; return Task.FromResult(new LaunchResult(true, null)); },
            _ => Task.FromResult<string?>(null),
            (path, category) => new LauncherEntry("custom:1", "x", path, null, null, "custom", true, false, category),
            _ => true,
            (_, _) => true));

        var result = await handler.HandleAsync("launchApp", Payload(new { id = "steam" }), CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync("setLauncherHidden", Payload(new { id = "steam" }), CancellationToken.None));

        Assert.Equal("steam", launchedId);
        Assert.True(Assert.IsType<LaunchResult>(result).Success);
    }

    [Fact]
    public async Task RpcHandler_adds_only_the_file_picked_by_the_host()
    {
        string? pickedPath = null;
        string? addedPath = null;
        var handler = new LauncherRpcHandler(new LauncherRpcActions(
            (_, _) => Task.FromResult<IReadOnlyList<LauncherEntry>>([]),
            (_, _) => Task.FromResult(new LaunchResult(true, null)),
            _ => Task.FromResult(pickedPath),
            (path, category) => { addedPath = path; return new LauncherEntry("custom:1", "Tool", path, null, null, "custom", true, false, category); },
            _ => true,
            (_, _) => true));

        string cancelled = JsonSerializer.Serialize(await handler.HandleAsync(
            "addCustomLauncher", Payload(new { path = @"\\evil\share\x.exe", category = "apps" }), CancellationToken.None), BridgeRpc.JsonOpts);
        Assert.Null(addedPath);
        Assert.Contains("\"added\":false", cancelled);

        pickedPath = @"D:\Tools\tool.exe";
        string added = JsonSerializer.Serialize(await handler.HandleAsync(
            "addCustomLauncher", Payload(new { path = @"\\evil\share\x.exe", category = "apps" }), CancellationToken.None), BridgeRpc.JsonOpts);
        Assert.Equal(@"D:\Tools\tool.exe", addedPath);
        Assert.Contains("\"added\":true", added);
        Assert.Contains("\"category\":\"apps\"", added);
    }

    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    private sealed class FakeLauncherEnvironment : ILauncherEnvironment
    {
        public Dictionary<(RegistryHive, string, string), string> Registry { get; } = new();
        public List<UninstallEntry> Uninstall { get; } = new();
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Texts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? ReadRegistryString(RegistryHive hive, string keyPath, string valueName)
            => Registry.TryGetValue((hive, keyPath, valueName), out var value) ? value : null;

        public IEnumerable<UninstallEntry> GetUninstallEntries() => Uninstall;
        public bool FileExists(string path) => Files.Contains(path);
        public string? ReadAllText(string path) => Texts.TryGetValue(path, out var text) ? text : null;

        public string GetFolderPath(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.ProgramFilesX86 => @"C:\Program Files (x86)",
            Environment.SpecialFolder.ProgramFiles => @"C:\Program Files",
            Environment.SpecialFolder.LocalApplicationData => @"C:\Users\test\AppData\Local",
            Environment.SpecialFolder.CommonApplicationData => @"C:\ProgramData",
            _ => "",
        };
    }
}
