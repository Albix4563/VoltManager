using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests.GameDetection;

/// <summary>
/// Deterministic classification matrix for storefronts, launchers, and real game binaries.
/// Covers start detection, launcher exclusion, sticky PID keep/drop, and PID-reuse safety.
/// </summary>
public class HeavyAppClassificationTests
{
    private static readonly HeavyAppDetectionSettings DefaultConfig = new();
    private static readonly HashSet<string> EmptyGpu = new(StringComparer.OrdinalIgnoreCase);
    private const long Mb = 1024L * 1024L;

    public static IEnumerable<object[]> StorefrontLaunchers()
    {
        yield return Row(
            @"C:\Program Files (x86)\Steam\steam.exe",
            "steam",
            400 * Mb,
            null);
        yield return Row(
            @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe",
            "steamwebhelper",
            500 * Mb,
            null);
        yield return Row(
            @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
            "EpicGamesLauncher",
            350 * Mb,
            null);
        yield return Row(
            @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe",
            "EADesktop",
            450 * Mb,
            null);
        yield return Row(
            @"C:\Program Files (x86)\Origin\Origin.exe",
            "Origin",
            300 * Mb,
            null);
        yield return Row(
            @"C:\Program Files\WindowsApps\Microsoft.GamingApp_1.0.0.0_x64__8wekyb3d8bbwe\XboxPcApp.exe",
            "XboxPcApp",
            250 * Mb,
            null);
        yield return Row(
            @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\upc.exe",
            "upc",
            280 * Mb,
            null);
        yield return Row(
            @"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe",
            "GalaxyClient",
            320 * Mb,
            null);
        yield return Row(
            @"C:\Program Files\Riot Games\Riot Client\RiotClientServices.exe",
            "RiotClientServices",
            400 * Mb,
            null);
        yield return Row(
            @"C:\Program Files\Riot Games\League of Legends\LeagueClient.exe",
            "LeagueClient",
            600 * Mb,
            null);
        yield return Row(
            @"C:\Program Files (x86)\Battle.net\Battle.net.exe",
            "Battle.net",
            500 * Mb,
            null);
        yield return Row(
            @"C:\Program Files\Rockstar Games\Launcher\Launcher.exe",
            "Launcher",
            200 * Mb,
            null);
    }

    public static IEnumerable<object[]> RealGames()
    {
        // Steam
        yield return Row(
            @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
            "Cyberpunk2077",
            800 * Mb,
            "gameInstallPath");
        // Epic
        yield return Row(
            @"D:\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe",
            "FortniteClient-Win64-Shipping",
            1200 * Mb,
            "gameInstallPath");
        // EA
        yield return Row(
            @"C:\Program Files\EA Games\EA SPORTS FC 25\FC25.exe",
            "FC25",
            900 * Mb,
            "gameInstallPath");
        // Xbox / MS store game root (not WindowsApps shell)
        yield return Row(
            @"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe",
            "ForzaHorizon5",
            1500 * Mb,
            "gameInstallPath");
        // Ubisoft
        yield return Row(
            @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Assassins Creed\AC.exe",
            "AC",
            1100 * Mb,
            "gameInstallPath");
        // GOG
        yield return Row(
            @"C:\GOG Games\Witcher 3\bin\x64\witcher3.exe",
            "witcher3",
            1000 * Mb,
            "gameInstallPath");
        // Riot VALORANT (shipping binary under Riot Games)
        yield return Row(
            @"C:\Program Files\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            "VALORANT-Win64-Shipping",
            1400 * Mb,
            "gameInstallPath");
        // LoL in-match process (must not be treated as LeagueClient)
        yield return Row(
            @"C:\Program Files\Riot Games\League of Legends\Game\League of Legends.exe",
            "League of Legends",
            1300 * Mb,
            "gameInstallPath");
        // Battle.net / Blizzard channel folder — sticky layout, no launcher parent required.
        yield return Row(
            @"D:\Games\World of Warcraft\_retail_\Wow.exe",
            "Wow",
            400 * Mb,
            "gameBinaryLayout");
        yield return Row(
            @"D:\Games\Overwatch\_retail_\Overwatch.exe",
            "Overwatch",
            500 * Mb,
            "gameBinaryLayout");
        yield return Row(
            @"D:\Games\World of Warcraft\_classic_\WowClassic.exe",
            "WowClassic",
            350 * Mb,
            "gameBinaryLayout");
        // Standalone Unreal layout outside any storefront
        yield return Row(
            @"E:\Indie\MyTitle\Binaries\Win64\MyTitle-Win64-Shipping.exe",
            "MyTitle-Win64-Shipping",
            700 * Mb,
            "gameBinaryLayout");
    }

    [Fact]
    public void Blizzard_channel_layout_stays_sticky_after_alt_tab_ws_drop()
    {
        var started = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"D:\Games\World of Warcraft\_retail_\Wow.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [3003] = new DetectedHeavyApp
            {
                ProcessId = 3003,
                Name = "Wow",
                Path = path,
                Reason = "gameBinaryLayout",
                Kind = "game",
                WorkingSetMb = 1600,
                StartedAtUtc = started,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(3003, path, started, "Wow", 90) },
            DateTime.UtcNow,
            minWorkingSetMb: 1536);

        Assert.Single(merged);
        Assert.Equal("gameBinaryLayout", merged[0].Reason);
        Assert.True(sticky.ContainsKey(3003));
    }

    [Fact]
    public void Battle_net_client_under_battle_net_path_never_classifies()
    {
        // Client tree is NonGame even if a channel-like folder name appears nearby.
        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"C:\Program Files (x86)\Battle.net\Battle.net.exe",
            "Battle.net",
            800 * Mb,
            EmptyGpu,
            DefaultConfig));
        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"C:\Program Files (x86)\Battle.net\Agent.exe",
            "Agent",
            200 * Mb,
            EmptyGpu,
            DefaultConfig));
    }

    [Theory]
    [MemberData(nameof(StorefrontLaunchers))]
    public void Storefront_launchers_never_classify_as_games(
        string path, string processName, long workingSetBytes, string? expected)
    {
        string? actual = HeavyAppDetectionService.ClassifyProcess(
            path, processName, workingSetBytes, EmptyGpu, DefaultConfig, hasLauncherAncestor: false);
        Assert.Equal(expected, actual);

        // Even with a bogus launcher ancestor, shells stay excluded.
        string? withAncestor = HeavyAppDetectionService.ClassifyProcess(
            path, processName, workingSetBytes, EmptyGpu, DefaultConfig, hasLauncherAncestor: true);
        Assert.Null(withAncestor);
    }

    [Theory]
    [MemberData(nameof(RealGames))]
    public void Real_games_are_detected(
        string path, string processName, long workingSetBytes, string? expected)
    {
        string? actual = HeavyAppDetectionService.ClassifyProcess(
            path, processName, workingSetBytes, EmptyGpu, DefaultConfig, hasLauncherAncestor: true);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Custom_folder_game_detected_via_launcher_ancestry_without_path_marker()
    {
        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyLibrary\CoolGame\CoolGame.exe",
            "CoolGame",
            900 * Mb,
            EmptyGpu,
            DefaultConfig,
            hasLauncherAncestor: true);

        Assert.Equal("launcherChild", reason);

        var assessment = HeavyAppDetectionService.AssessProcess(
            @"D:\MyLibrary\CoolGame\CoolGame.exe",
            "CoolGame",
            900 * Mb,
            EmptyGpu,
            DefaultConfig,
            DateTime.UtcNow.AddMinutes(-5),
            DateTime.UtcNow,
            hasLauncherAncestor: true);

        Assert.Equal("launcherChild", assessment.PrimaryReason);
        Assert.Contains(assessment.Evidence, e => e.Code == "launcherAncestry");
        Assert.True(assessment.Score >= 15);
    }

    [Fact]
    public void Custom_folder_game_detected_via_foreground_without_launcher_or_path()
    {
        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyLibrary\CoolGame\CoolGame.exe",
            "CoolGame",
            900 * Mb,
            EmptyGpu,
            DefaultConfig,
            hasLauncherAncestor: false,
            isForeground: true);

        Assert.Equal("foregroundActive", reason);

        var assessment = HeavyAppDetectionService.AssessProcess(
            @"D:\MyLibrary\CoolGame\CoolGame.exe",
            "CoolGame",
            900 * Mb,
            EmptyGpu,
            DefaultConfig,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow,
            hasLauncherAncestor: false,
            isForeground: true);

        Assert.Equal("foregroundActive", assessment.PrimaryReason);
        Assert.Contains(assessment.Evidence, e => e.Code == "foreground");
        Assert.True(assessment.Score >= 15);
    }

    [Fact]
    public void Foreground_does_not_classify_browsers_or_storefronts()
    {
        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "chrome",
            2000 * Mb,
            EmptyGpu,
            DefaultConfig,
            isForeground: true));
        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"C:\Program Files (x86)\Steam\steam.exe",
            "steam",
            500 * Mb,
            EmptyGpu,
            DefaultConfig,
            isForeground: true));
        Assert.Null(HeavyAppDetectionService.ClassifyProcess(
            @"C:\Program Files (x86)\Battle.net\Battle.net.exe",
            "Battle.net",
            500 * Mb,
            EmptyGpu,
            DefaultConfig,
            isForeground: true));
    }

    [Fact]
    public void Sticky_foreground_game_survives_alt_tab_then_clears_on_exit()
    {
        var started = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"D:\MyLibrary\CoolGame\CoolGame.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [4004] = new DetectedHeavyApp
            {
                ProcessId = 4004,
                Name = "CoolGame",
                Path = path,
                Reason = "foregroundActive",
                Kind = "game",
                WorkingSetMb = 900,
                StartedAtUtc = started,
            },
        };

        // Alt-tab: no longer foreground, WS drops — sticky must keep the session.
        var minimized = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(4004, path, started, "CoolGame", 80) },
            DateTime.UtcNow,
            minWorkingSetMb: 1536);
        Assert.Single(minimized);
        Assert.Equal("foregroundActive", minimized[0].Reason);

        var afterExit = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            Array.Empty<ObservedHeavyProcess>(),
            DateTime.UtcNow);
        Assert.Empty(afterExit);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Shipping_binary_in_foreground_classifies_even_with_low_ws()
    {
        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"E:\Indie\MyTitle\MyTitle-Win64-Shipping.exe",
            "MyTitle-Win64-Shipping",
            180 * Mb,
            EmptyGpu,
            DefaultConfig,
            isForeground: true);

        Assert.Equal("foregroundActive", reason);
    }

    [Fact]
    public void Tiny_helper_under_launcher_parent_is_not_launcherChild()
    {
        string? reason = HeavyAppDetectionService.ClassifyProcess(
            @"D:\MyLibrary\CoolGame\crashpad_handler.exe",
            "crashpad_handler",
            40 * Mb,
            EmptyGpu,
            DefaultConfig,
            hasLauncherAncestor: true);

        Assert.Null(reason);
    }

    [Fact]
    public void Sticky_keeps_game_when_working_set_drops_after_alt_tab()
    {
        var started = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"D:\SteamLibrary\steamapps\common\Title\game.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [1001] = new DetectedHeavyApp
            {
                ProcessId = 1001,
                Name = "game",
                Path = path,
                Reason = "gameInstallPath",
                Kind = "game",
                WorkingSetMb = 2000,
                StartedAtUtc = started,
            },
        };

        // Same PID still alive, path/start unchanged, but WS collapsed after minimize.
        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(1001, path, started, "game", 120) },
            DateTime.UtcNow,
            minWorkingSetMb: 1536);

        Assert.Single(merged);
        Assert.Equal(1001, merged[0].ProcessId);
        Assert.Equal("gameInstallPath", merged[0].Reason);
        Assert.True(sticky.ContainsKey(1001));
    }

    [Fact]
    public void Sticky_drops_on_real_exit()
    {
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [55] = new DetectedHeavyApp
            {
                ProcessId = 55,
                Name = "game",
                Path = @"D:\Games\Title\game.exe",
                Reason = "gameInstallPath",
                WorkingSetMb = 1800,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            Array.Empty<ObservedHeavyProcess>(),
            DateTime.UtcNow);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Sticky_handoff_bootstrap_to_shipping_under_same_install_root()
    {
        var sessionStart = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string bootstrap = @"D:\SteamLibrary\steamapps\common\Title\Title.exe";
        string shipping = @"D:\SteamLibrary\steamapps\common\Title\Binaries\Win64\Title-Win64-Shipping.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [5001] = new DetectedHeavyApp
            {
                ProcessId = 5001,
                Name = "Title",
                Path = bootstrap,
                Reason = "gameInstallPath",
                Kind = "game",
                WorkingSetMb = 400,
                StartedAtUtc = sessionStart,
            },
        };

        // Bootstrap PID gone; shipping binary still live under the same Steam title folder.
        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[]
            {
                new ObservedHeavyProcess(5002, shipping, sessionStart.AddSeconds(2), "Title-Win64-Shipping", 600),
            },
            DateTime.UtcNow,
            minWorkingSetMb: 1536);

        Assert.Single(merged);
        Assert.Equal(5002, merged[0].ProcessId);
        Assert.Equal("gameInstallPath", merged[0].Reason);
        Assert.False(sticky.ContainsKey(5001));
        Assert.True(sticky.ContainsKey(5002));
    }

    [Fact]
    public void Sticky_handoff_skips_helpers_and_unrelated_paths()
    {
        var sessionStart = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string bootstrap = @"D:\SteamLibrary\steamapps\common\Title\Title.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [5101] = new DetectedHeavyApp
            {
                ProcessId = 5101,
                Name = "Title",
                Path = bootstrap,
                Reason = "gameInstallPath",
                WorkingSetMb = 400,
                StartedAtUtc = sessionStart,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[]
            {
                // Same install root but helper — must not inherit sticky.
                new ObservedHeavyProcess(
                    5102,
                    @"D:\SteamLibrary\steamapps\common\Title\EasyAntiCheat\EasyAntiCheat_EOS.exe",
                    sessionStart.AddSeconds(1),
                    "EasyAntiCheat_EOS",
                    120),
                // Different game entirely.
                new ObservedHeavyProcess(
                    5103,
                    @"D:\SteamLibrary\steamapps\common\OtherGame\Other.exe",
                    sessionStart.AddSeconds(1),
                    "Other",
                    900),
            },
            DateTime.UtcNow,
            minWorkingSetMb: 1536);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Sticky_handoff_blizzard_channel_to_peer_binary()
    {
        var sessionStart = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string first = @"D:\Games\World of Warcraft\_retail_\Wow.exe";
        string peer = @"D:\Games\World of Warcraft\_retail_\WowClassic.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [5201] = new DetectedHeavyApp
            {
                ProcessId = 5201,
                Name = "Wow",
                Path = first,
                Reason = "gameBinaryLayout",
                Kind = "game",
                WorkingSetMb = 800,
                StartedAtUtc = sessionStart,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(5202, peer, sessionStart.AddSeconds(3), "WowClassic", 700) },
            DateTime.UtcNow);

        Assert.Single(merged);
        Assert.Equal(5202, merged[0].ProcessId);
        Assert.Equal("gameBinaryLayout", merged[0].Reason);
    }

    [Fact]
    public void TryGetGameInstallRoot_prefers_storefront_title_folder()
    {
        string? root = HeavyAppDetectionService.TryGetGameInstallRoot(
            @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe");
        Assert.Equal(
            HeavyAppDetectionService.NormalizePath(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077"),
            root);

        string? unrealRoot = HeavyAppDetectionService.TryGetGameInstallRoot(
            @"E:\Indie\MyTitle\Binaries\Win64\MyTitle-Win64-Shipping.exe");
        Assert.Equal(
            HeavyAppDetectionService.NormalizePath(@"E:\Indie\MyTitle"),
            unrealRoot);
    }

    [Fact]
    public void Sticky_drops_on_pid_reuse_with_different_start_time()
    {
        var originalStart = new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc);
        var reuseStart = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"D:\Games\Title\game.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [77] = new DetectedHeavyApp
            {
                ProcessId = 77,
                Name = "game",
                Path = path,
                Reason = "gameInstallPath",
                WorkingSetMb = 1800,
                StartedAtUtc = originalStart,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(77, path, reuseStart, "game", 1800) },
            DateTime.UtcNow);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Sticky_drops_storefront_shell_even_if_previously_misclassified()
    {
        string path = @"C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [42] = new DetectedHeavyApp
            {
                ProcessId = 42,
                Name = "EADesktop",
                Path = path,
                Reason = "gameInstallPath",
                WorkingSetMb = 400,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(42, path, DateTime.UtcNow, "EADesktop", 400) },
            DateTime.UtcNow);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Sticky_launcherChild_survives_ws_drop_then_clears_on_exit()
    {
        var started = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);
        string path = @"D:\Custom\CoolGame\CoolGame.exe";
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [2002] = new DetectedHeavyApp
            {
                ProcessId = 2002,
                Name = "CoolGame",
                Path = path,
                Reason = "launcherChild",
                Kind = "game",
                WorkingSetMb = 900,
                StartedAtUtc = started,
            },
        };

        var minimized = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(2002, path, started, "CoolGame", 80) },
            DateTime.UtcNow);
        Assert.Single(minimized);
        Assert.Equal("launcherChild", minimized[0].Reason);

        var afterExit = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            Array.Empty<ObservedHeavyProcess>(),
            DateTime.UtcNow);
        Assert.Empty(afterExit);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Resource_heuristic_hits_are_not_sticky()
    {
        var sticky = new Dictionary<int, DetectedHeavyApp>
        {
            [9] = new DetectedHeavyApp
            {
                ProcessId = 9,
                Name = "bigtool",
                Path = @"C:\Tools\bigtool.exe",
                Reason = "resourceHeuristic",
                WorkingSetMb = 2048,
            },
        };

        var merged = HeavyAppDetectionService.MergeStickyDetections(
            sticky,
            Array.Empty<DetectedHeavyApp>(),
            new[] { new ObservedHeavyProcess(9, @"C:\Tools\bigtool.exe", DateTime.UtcNow, "bigtool", 200) },
            DateTime.UtcNow,
            minWorkingSetMb: 1536);

        Assert.Empty(merged);
        Assert.Empty(sticky);
    }

    [Fact]
    public void Gpu_preference_still_outranks_install_path()
    {
        string path = HeavyAppDetectionService.NormalizePath(
            @"D:\SteamLibrary\steamapps\common\Title\game.exe");
        var gpu = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };

        string? reason = HeavyAppDetectionService.ClassifyProcess(
            path, "game", 200 * Mb, gpu, DefaultConfig);

        Assert.Equal("windowsGpuPreference", reason);
    }

    private static object[] Row(string path, string name, long ws, string? expected)
        => new object[] { path, name, ws, expected! };
}
