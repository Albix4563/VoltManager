namespace VoltManager.Services.GameDetection;

/// <summary>
/// Path and process-name signatures used by <see cref="VoltManager.Services.HeavyAppDetectionService"/>.
/// Data only — the matching logic lives with the classifier. All entries are lowercase and
/// matched as substrings against a normalized path, or as exact process names.
/// </summary>
internal static class GameSignatures
{
    // Game content roots only (substring match on normalized path).
    // Do NOT list storefront/client folders here — those keep the high-performance plan
    // stuck on after a session (e.g. EA Desktop next to FIFA / EA FC).
    public static readonly string[] GamePathMarkers =
    {
        @"\steamapps\common\",
        @"\steamapps\sourcemods\",
        @"\epic games\",
        @"\legendary\",
        @"\gog galaxy\games\",
        @"\gog games\",
        @"\xboxgames\",
        @"\xbox games\",
        @"\riot games\",
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\origin games\",
        @"\.minecraft\",
        @"\itch\apps\",
        @"\amazon games\library\",
        @"\rockstar games\",
        @"\bethesda.net\",
        @"\square enix\",
        @"\bandai namco\",
        @"\paradox interactive\",
        @"\2k games\",
        @"\wargaming.net\",
        @"\roblox\",
        @"\oculus\software\",
        @"\meta quest\",
        @"\mihoyo\",
        @"\hoyoverse\",
        @"\genshin impact\",
        @"\honkai\",
        @"\netease\",
        @"\garena\",
        @"\microsoft games\",
        // ponytail: no bare \windowsapps\ — UWP shells (Netflix/Spotify) live there; Xbox uses \xboxgames\
    };

    // Engine / shipping binary layouts common to Unreal, many AAA, etc.
    // Blizzard channel folders (\_retail_\, \_classic_\, …) are install-layout signals for
    // Battle.net titles living outside any storefront path marker — sticky without RAM peak.
    public static readonly string[] GameBinaryLayouts =
    {
        @"\binaries\win64\",
        @"\binaries\win32\",
        @"\bin\win64\",
        @"\bin\win32\",
        @"\engine\binaries\win64\",
        @"\engine\binaries\win32\",
        @"\game\bin\",
        @"\shipping\",
        @"\_retail_\",
        @"\_classic_\",
        @"\_classic_era_\",
        @"\_classic_ptr_\",
        @"\_ptr_\",
        @"\_beta_\",
        @"\_vendor_\",
    };

    // Paths that look like games but are storefront/system shells we never treat as heavy.
    // Checked before GamePathMarkers so launcher roots under shared prefixes (Epic, Rockstar,
    // Riot, …) never force the performance plan while idling in the background.
    public static readonly string[] NonGamePathMarkers =
    {
        @"\windowsapps\microsoft.",
        @"\windowsapps\microsoftwindows.",
        @"\windowsapps\microsoft.windows",
        @"\windowsapps\microsoft.bing",
        @"\windowsapps\microsoft.office",
        @"\windowsapps\microsoft.skypeapp",
        @"\windowsapps\microsoft.zune",
        @"\windowsapps\microsoft.yourphone",
        @"\windowsapps\microsoft.gamingapp",
        @"\windowsapps\microsoft.xbox",
        @"\edgewebview\",
        @"\edge\application\",
        // Storefront clients / helpers (not playable game content)
        @"\ea desktop\",
        @"\electronic arts\ea desktop\",
        @"\origin\origin.exe",
        @"\origin\originwebhelperservice",
        @"\epic games\launcher\",
        @"\epic games\epic games launcher\",
        @"\battle.net\",
        @"\blizzard entertainment\battle.net\",
        @"\riot games\riot client\",
        @"\riot games\league of legends\leagueclient",
        @"\riot games\league of legends\riot client",
        @"\rockstar games\launcher\",
        @"\rockstar games\social club\",
        @"\ubisoft connect\",
        @"\ubisoft game launcher\upc.exe",
        @"\ubisoft game launcher\ubisoftconnect",
        @"\gog galaxy\galaxyclient",
        @"\steam\bin\",
        @"\steam\steam.exe",
        @"\steam\steamapps\common\steamworks shared\",
        @"\amazon games\app\",
        @"\itch\butler",
    };

    // Known non-game workloads: heavy, but never a game whatever they do on the GPU.
    public static readonly string[] ResourceDenyPathMarkers =
    {
        @"\windows\",
        @"\microsoft\edge\",
        @"\google\chrome\",
        @"\microsoft\edgewebview\",
        @"\mozilla firefox\",
        @"\brave software\",
        @"\vivaldi\",
        @"\opera\",
        @"\chromium\",
        @"\msedge.exe",
        @"\teams\",
        @"\microsoft teams\",
        @"\slack\",
        @"\zoom\",
        @"\discord\",
        @"\spotify\",
        @"\dropbox\",
        @"\onedrive\",
        @"\code.exe",
        @"\devenv.exe",
        @"\jetbrains\",
    };

    public static readonly string[] ResourceDenyProcessNames =
    {
        "explorer", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
        "runtimebroker", "sihost", "taskhostw", "dwm", "csrss", "lsass", "services",
        "svchost", "system", "registry", "smss", "winlogon", "fontdrvhost",
        "msedge", "chrome", "firefox", "brave", "opera", "vivaldi",
        "teams", "ms-teams", "slack", "zoom", "discord", "spotify",
        "code", "devenv", "voltmanager",
    };
}
