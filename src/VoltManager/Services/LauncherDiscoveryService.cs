#pragma warning disable CA1416
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>Read-only view of the machine used to locate game launchers; injectable for tests.</summary>
internal interface ILauncherEnvironment
{
    /// <summary>Reads a string value from HKLM/HKCU, trying the 64-bit then the 32-bit registry view.</summary>
    string? ReadRegistryString(RegistryHive hive, string keyPath, string valueName);

    /// <summary>DisplayName / InstallLocation / DisplayIcon of every Uninstall entry (HKLM 64+32, HKCU).</summary>
    IEnumerable<UninstallEntry> GetUninstallEntries();

    bool FileExists(string path);
    string? ReadAllText(string path);
    string GetFolderPath(Environment.SpecialFolder folder);
}

internal sealed record UninstallEntry(string DisplayName, string? InstallLocation, string? DisplayIcon);

internal sealed record LauncherDefinition(
    string Id,
    string Name,
    Func<ILauncherEnvironment, IEnumerable<string?>> Candidates);

/// <summary>
/// Finds the well-known game launchers installed on this PC and merges them with the
/// user's custom apps. Launching only ever accepts an id from that merged list, never a
/// path coming from the web UI.
/// </summary>
internal sealed class LauncherDiscoveryService
{
    public const string LimitReachedMessage = "limit";

    private readonly SettingsService _settings;
    private readonly ILauncherEnvironment _environment;
    private readonly Func<string, string?> _iconLoader;
    private readonly Action<string, string?> _launcher;
    private readonly IReadOnlyList<LauncherDefinition> _catalog;
    private readonly ConcurrentDictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _detectGate = new(1, 1);
    private IReadOnlyList<(LauncherDefinition Definition, string Path)>? _detected;

    public event Action? Changed;

    public LauncherDiscoveryService(SettingsService settings)
        : this(settings, new SystemLauncherEnvironment(), ShellIconLoader.TryGetPngDataUrl, StartProcess, DefaultCatalog)
    {
    }

    internal LauncherDiscoveryService(
        SettingsService settings,
        ILauncherEnvironment environment,
        Func<string, string?> iconLoader,
        Action<string, string?> launcher,
        IReadOnlyList<LauncherDefinition> catalog)
    {
        _settings = settings;
        _environment = environment;
        _iconLoader = iconLoader;
        _launcher = launcher;
        _catalog = catalog;
    }

    public async Task<IReadOnlyList<LauncherEntry>> GetLaunchersAsync(bool refresh, CancellationToken ct = default)
    {
        var detected = await EnsureDetectedAsync(refresh, ct).ConfigureAwait(false);
        return await Task.Run(() => BuildEntries(detected), ct).ConfigureAwait(false);
    }

    public async Task<LaunchResult> LaunchAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new LaunchResult(false, "unknown");

        var detected = await EnsureDetectedAsync(false, ct).ConfigureAwait(false);
        string? path = null;
        string? args = null;

        var match = detected.FirstOrDefault(d => string.Equals(d.Definition.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match.Definition != null)
        {
            path = match.Path;
        }
        else
        {
            var custom = _settings.Current.Launcher.CustomApps
                .FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (custom != null)
                path = custom.Path;
        }

        if (path == null)
            return new LaunchResult(false, "unknown");
        if (!_environment.FileExists(path))
            return new LaunchResult(false, "missing");

        try
        {
            await Task.Run(() => _launcher(path, args), ct).ConfigureAwait(false);
            return new LaunchResult(true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error("Launcher start failed: " + id, ex);
            return new LaunchResult(false, "failed");
        }
    }

    public LauncherEntry AddCustom(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path");

        string fullPath = Path.GetFullPath(path.Trim());
        if (!LauncherSettings.IsAllowedCustomPath(fullPath))
            throw new ArgumentException("extension");
        if (!_environment.FileExists(fullPath))
            throw new FileNotFoundException(fullPath);

        string id = LauncherSettings.CustomIdFor(fullPath);
        string displayName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(fullPath)
            : name.Trim();

        _settings.Update(s =>
        {
            s.Launcher ??= new LauncherSettings();
            if (s.Launcher.CustomApps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
                return;
            if (s.Launcher.CustomApps.Count >= LauncherSettings.MaxCustomApps)
                throw new InvalidOperationException(LimitReachedMessage);
            s.Launcher.CustomApps.Add(new CustomLauncherApp { Id = id, Name = displayName, Path = fullPath });
        });

        RaiseChanged();
        var stored = _settings.Current.Launcher.CustomApps.First(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        return ToEntry(stored, _settings.Current.Launcher.HiddenIds);
    }

    public bool RemoveCustom(string id)
    {
        bool removed = false;
        _settings.Update(s =>
        {
            removed = s.Launcher.CustomApps.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                s.Launcher.HiddenIds.RemoveAll(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase));
        });
        if (removed)
            RaiseChanged();
        return removed;
    }

    public bool SetHidden(string id, bool hidden)
    {
        if (string.IsNullOrWhiteSpace(id) || !IsKnownId(id))
            return false;

        _settings.Update(s =>
        {
            s.Launcher.HiddenIds.RemoveAll(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase));
            if (hidden)
                s.Launcher.HiddenIds.Add(id);
        });
        RaiseChanged();
        return true;
    }

    private bool IsKnownId(string id)
        => _catalog.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
           || _settings.Current.Launcher.CustomApps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<(LauncherDefinition Definition, string Path)>> EnsureDetectedAsync(bool refresh, CancellationToken ct)
    {
        var cached = _detected;
        if (cached != null && !refresh)
            return cached;

        await _detectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_detected != null && !refresh)
                return _detected;

            var result = await Task.Run(Detect, ct).ConfigureAwait(false);
            _detected = result;
            if (refresh)
                _icons.Clear();
            return result;
        }
        finally
        {
            _detectGate.Release();
        }
    }

    internal IReadOnlyList<(LauncherDefinition Definition, string Path)> Detect()
    {
        var found = new List<(LauncherDefinition, string)>();
        var environment = new UninstallSnapshotEnvironment(_environment);
        foreach (var definition in _catalog)
        {
            try
            {
                string? path = definition.Candidates(environment)
                    .Select(NormalizeCandidate)
                    .FirstOrDefault(p => p != null && _environment.FileExists(p));
                if (path != null)
                    found.Add((definition, path));
            }
            catch (Exception ex)
            {
                Logger.Warn("Launcher detection failed: " + definition.Id + " - " + ex.Message);
            }
        }
        return found;
    }

    private IReadOnlyList<LauncherEntry> BuildEntries(IReadOnlyList<(LauncherDefinition Definition, string Path)> detected)
    {
        var launcher = _settings.Current.Launcher;
        var entries = new List<LauncherEntry>();
        foreach (var (definition, path) in detected)
        {
            entries.Add(new LauncherEntry(
                definition.Id,
                definition.Name,
                path,
                null,
                GetIcon(path),
                "detected",
                true,
                IsHidden(launcher.HiddenIds, definition.Id)));
        }

        foreach (var app in launcher.CustomApps)
            entries.Add(ToEntry(app, launcher.HiddenIds));

        return entries;
    }

    private LauncherEntry ToEntry(CustomLauncherApp app, List<string> hiddenIds)
    {
        bool available = _environment.FileExists(app.Path);
        return new LauncherEntry(
            app.Id,
            app.Name,
            app.Path,
            null,
            available ? GetIcon(app.Path) : null,
            "custom",
            available,
            IsHidden(hiddenIds, app.Id));
    }

    private static bool IsHidden(List<string> hiddenIds, string id)
        => hiddenIds.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase));

    private string? GetIcon(string path)
        => _icons.GetOrAdd(path, p =>
        {
            try { return _iconLoader(p); }
            catch { return null; }
        });

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Error("launchersChanged handler failed", ex); }
    }

    internal static string? NormalizeCandidate(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        string value = candidate.Trim();
        // DisplayIcon values look like "\"C:\\x\\app.exe\",0".
        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
            value = value[..comma];
        value = value.Trim().Trim('"').Replace('/', '\\');

        try
        {
            return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void StartProcess(string path, string? args)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        if (!string.IsNullOrWhiteSpace(args))
            info.Arguments = args;
        using var _ = Process.Start(info);
    }

    // ── Catalog ─────────────────────────────────────────────────────────────

    internal static readonly IReadOnlyList<LauncherDefinition> DefaultCatalog =
    [
        new("steam", "Steam", env =>
        [
            env.ReadRegistryString(RegistryHive.CurrentUser, @"Software\Valve\Steam", "SteamExe"),
            Combine(env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"), "steam.exe"),
            .. FromUninstall(env, "Steam", "steam.exe"),
            .. Defaults(env, @"Steam\steam.exe"),
        ]),
        new("epic", "Epic Games", env =>
        [
            .. FromUninstall(env, "Epic Games Launcher",
                @"Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                @"Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"),
            .. Defaults(env,
                @"Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe",
                @"Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe"),
        ]),
        new("ea", "EA app", env =>
        [
            env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "DesktopAppPath"),
            Combine(env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "InstallLocation"), @"EA Desktop\EADesktop.exe"),
            .. FromUninstall(env, "EA app", @"EA Desktop\EADesktop.exe", "EADesktop.exe"),
            .. Defaults(env, @"Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe"),
        ]),
        new("ubisoft", "Ubisoft Connect", env =>
        [
            Combine(env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Ubisoft\Launcher", "InstallDir"), "UbisoftConnect.exe"),
            Combine(env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Ubisoft\Launcher", "InstallDir"), "upc.exe"),
            .. FromUninstall(env, "Ubisoft Connect", "UbisoftConnect.exe", "upc.exe"),
            .. Defaults(env, @"Ubisoft\Ubisoft Game Launcher\UbisoftConnect.exe", @"Ubisoft\Ubisoft Game Launcher\upc.exe"),
        ]),
        new("gog", "GOG Galaxy", env =>
        [
            Combine(env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\GOG.com\GalaxyClient\paths", "client"), "GalaxyClient.exe"),
            .. FromUninstall(env, "GOG GALAXY", "GalaxyClient.exe"),
            .. Defaults(env, @"GOG Galaxy\GalaxyClient.exe"),
        ]),
        new("battlenet", "Battle.net", env =>
        [
            .. FromUninstall(env, "Battle.net", "Battle.net Launcher.exe", "Battle.net.exe"),
            .. Defaults(env, @"Battle.net\Battle.net Launcher.exe", @"Battle.net\Battle.net.exe"),
        ]),
        new("riot", "Riot Client", env =>
        [
            .. FromRiotInstalls(env),
            .. FromUninstall(env, "Riot Client", @"Riot Client\RiotClientServices.exe", "RiotClientServices.exe"),
            @"C:\Riot Games\Riot Client\RiotClientServices.exe",
        ]),
        new("rockstar", "Rockstar Games", env =>
        [
            Combine(env.ReadRegistryString(RegistryHive.LocalMachine, @"SOFTWARE\Rockstar Games\Launcher", "InstallFolder"), "Launcher.exe"),
            .. FromUninstall(env, "Rockstar Games Launcher", "Launcher.exe"),
            .. Defaults(env, @"Rockstar Games\Launcher\Launcher.exe"),
        ]),
    ];

    private static string? Combine(string? directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;
        try { return Path.Combine(directory.Trim().Trim('"'), relative); }
        catch { return null; }
    }

    private static IEnumerable<string?> Defaults(ILauncherEnvironment env, params string[] relatives)
    {
        string[] roots =
        [
            env.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            env.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            env.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ];
        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (string relative in relatives)
                yield return Combine(root, relative);
    }

    private static IEnumerable<string?> FromUninstall(ILauncherEnvironment env, string displayName, params string[] relatives)
    {
        foreach (var entry in env.GetUninstallEntries())
        {
            if (!string.Equals(entry.DisplayName?.Trim(), displayName, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string relative in relatives)
                yield return Combine(entry.InstallLocation, relative);

            // DisplayIcon usually points at the launcher executable itself.
            string? icon = NormalizeCandidate(entry.DisplayIcon);
            if (icon != null && relatives.Any(r => string.Equals(Path.GetFileName(icon), Path.GetFileName(r), StringComparison.OrdinalIgnoreCase)))
                yield return icon;
        }
    }

    private static IEnumerable<string?> FromRiotInstalls(ILauncherEnvironment env)
    {
        string programData = env.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
            return [];

        string? json = env.ReadAllText(Path.Combine(programData, @"Riot Games\RiotClientInstalls.json"));
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var paths = new List<string?>();
            foreach (string key in new[] { "rc_default", "rc_live" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    paths.Add(value.GetString());
            }
            return paths;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Reads the Uninstall registry tree once per detection pass instead of once per launcher.</summary>
internal sealed class UninstallSnapshotEnvironment(ILauncherEnvironment inner) : ILauncherEnvironment
{
    private List<UninstallEntry>? _entries;

    public string? ReadRegistryString(RegistryHive hive, string keyPath, string valueName)
        => inner.ReadRegistryString(hive, keyPath, valueName);

    public IEnumerable<UninstallEntry> GetUninstallEntries()
        => _entries ??= inner.GetUninstallEntries().ToList();

    public bool FileExists(string path) => inner.FileExists(path);
    public string? ReadAllText(string path) => inner.ReadAllText(path);
    public string GetFolderPath(Environment.SpecialFolder folder) => inner.GetFolderPath(folder);
}

internal sealed class SystemLauncherEnvironment : ILauncherEnvironment
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public string? ReadRegistryString(RegistryHive hive, string keyPath, string valueName)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(keyPath);
                if (key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                    return value;
            }
            catch
            {
                // Missing or unreadable keys simply mean "not found".
            }
        }
        return null;
    }

    public IEnumerable<UninstallEntry> GetUninstallEntries()
    {
        var entries = new List<UninstallEntry>();
        var sources = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
        };
        foreach (var (hive, view) in sources)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall == null)
                    continue;

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var app = uninstall.OpenSubKey(name);
                        if (app?.GetValue("DisplayName") is not string displayName)
                            continue;
                        entries.Add(new UninstallEntry(
                            displayName,
                            app.GetValue("InstallLocation") as string,
                            app.GetValue("DisplayIcon") as string));
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
        return entries;
    }

    public bool FileExists(string path) => File.Exists(path);

    public string? ReadAllText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
}

/// <summary>Extracts a file's shell icon as a small PNG data URL without System.Drawing.</summary>
internal static class ShellIconLoader
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static string? TryGetPngDataUrl(string path)
    {
        var info = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
