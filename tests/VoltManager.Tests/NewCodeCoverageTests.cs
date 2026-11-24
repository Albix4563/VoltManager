using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Win32;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.LanRemote;

namespace VoltManager.Tests;

public sealed class NewCodeCoverageTests
{
    private static readonly string[] EmptyThemeCatalog = ["blue"];

    [Fact]
    public void PeriodicScanRunner_catches_scan_exception_and_recovers_on_next_run()
    {
        int calls = 0;
        var runner = new PeriodicScanRunner(
            TimeSpan.FromSeconds(1),
            () =>
            {
                if (++calls == 1)
                    throw new InvalidOperationException("synthetic scan failure");
            },
            "Synthetic periodic scan failed");

        runner.RunOnce();
        runner.RunOnce();

        Assert.Equal(2, calls);
    }

    [Fact]
    public void HeavyAppDetectionService_Start_and_Stop_complete_cleanly()
    {
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        try
        {
            settings.Update(s => s.HeavyAppDetection.Enabled = false);
            using var service = new HeavyAppDetectionService(settings);

            service.Start();
            service.Stop();

            Assert.False(service.Current.Enabled);
            Assert.False(service.Current.Active);
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void ProcessPathResolver_TryQueryFullProcessImageName_resolves_current_process_and_rejects_invalid_pid()
    {
        string currentPath = ProcessPathResolver.TryQueryFullProcessImageName(Environment.ProcessId);
        string missingPath = ProcessPathResolver.TryQueryFullProcessImageName(-1);

        Assert.False(string.IsNullOrWhiteSpace(currentPath));
        Assert.True(File.Exists(currentPath));
        Assert.Equal(string.Empty, missingPath);
    }

    [Fact]
    public void LauncherDiscoveryService_AddCustom_rejects_empty_path()
    {
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        try
        {
            var service = new LauncherDiscoveryService(
                settings,
                new NullLauncherEnvironment(),
                _ => null,
                (_, _) => { },
                LauncherDiscoveryService.DefaultCatalog);

            Assert.Throws<ArgumentException>(() => service.AddCustom("   ", null));
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void ShellIconLoader_TryGetPngDataUrl_returns_null_for_missing_file()
    {
        string missingExe = Path.Combine(Path.GetTempPath(), $"voltmanager-missing-{Guid.NewGuid():N}.exe");

        string? dataUrl = ShellIconLoader.TryGetPngDataUrl(missingExe);

        Assert.Null(dataUrl);
    }

    [Theory]
    [InlineData("1.2.3-alpha.1", "1.2.3")]
    [InlineData("1.2.3+build.42", "1.2.3")]
    [InlineData("1.2.3-rc.1+build", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    public void UpdateService_VersionCore_strips_prerelease_and_metadata_suffixes(string version, string expected)
    {
        Assert.Equal(expected, UpdateService.VersionCore(version));
    }

    [Fact]
    public void UpdateSessionProtocol_rejects_quoted_legacy_arguments_and_null_json_palette()
    {
        string id = UpdateSessionProtocol.NewId();
        string nullJsonBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("null"));

        Assert.Throws<ArgumentException>(() => UpdateSessionProtocol.BuildArguments("/update \"bad\"", id, null));
        Assert.False(UpdateSessionProtocol.TryDecodePalette(nullJsonBase64, out string[] decoded));
        Assert.Empty(decoded);
    }

    [Fact]
    public void PowerActionExecutor_default_constructor_validates_arguments_without_side_effects()
    {
        var executor = new PowerActionExecutor();

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Execute(ScheduledPowerActionType.Sleep, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Execute((ScheduledPowerActionType)999));
    }

    [Fact]
    public async Task SettingsRpcHandler_setThemeColor_falls_back_to_blue_for_unknown_color()
    {
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        try
        {
            settings.Update(state => state.ThemeColor = AppThemeColor.Red);
            var handler = new SettingsRpcHandler(
                settings,
                new LocalizationService(),
                new SettingsRpcActions(
                    GetWebTheme: () => new { color = "blue" },
                    GetWebThemeCatalog: () => EmptyThemeCatalog,
                    RefreshAppPowerProfiles: () => { },
                    RefreshHeavyAppDetection: () => { },
                    RebuildJumpList: () => { },
                    IsStartWithWindowsEnabled: () => false,
                    SetStartWithWindows: enabled => enabled,
                    UtcNow: () => new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)),
                new NullBridgeDialogs());

            JsonElement payload = JsonSerializer.SerializeToElement(
                new { themeColor = "unknown-theme" },
                BridgeRpc.JsonOpts);

            object? response = await handler.HandleAsync("setThemeColor", payload, CancellationToken.None);

            Assert.NotNull(response);
            Assert.Equal(AppThemeColor.Blue, settings.Current.ThemeColor);
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public void VoltManagerArtifacts_handles_invalid_temp_root_and_missing_shutdown_event()
    {
        string[] cleanupFailures = VoltManagerArtifacts.CleanupOwnedTempArtifacts(string.Empty, string.Empty);
        string[] found = VoltManagerArtifacts.FindOwnedTempArtifacts(string.Empty, string.Empty);

        Assert.Single(cleanupFailures);
        Assert.StartsWith("Temp directory:", cleanupFailures[0], StringComparison.Ordinal);
        Assert.Empty(found);
        Assert.False(VoltManagerArtifacts.TrySignalShutdownEvent($"VoltManager_Missing_{Guid.NewGuid():N}"));
        Assert.False(VoltManagerArtifacts.TrySignalShutdownEvent(string.Empty));
        Assert.False(string.IsNullOrWhiteSpace(VoltManagerArtifacts.StartMenuDirectory));
        Assert.False(string.IsNullOrWhiteSpace(VoltManagerArtifacts.DesktopShortcutPath));
    }

    [Fact]
    public async Task LanRemoteControlService_ExecutePowerActionAsync_audits_rejected_authorization_when_request_aborted()
    {
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        var audit = new List<string>();
        try
        {
            using var service = new LanRemoteControlService(
                settings,
                CreateLanActions(),
                auth: new LanRemoteAuthStore(Path.Combine(root, "remote-control-auth.json")),
                auditLogger: audit.Add);

            MethodInfo? executePowerAction = typeof(LanRemoteControlService).GetMethod(
                "ExecutePowerActionAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(executePowerAction);

            var context = new DefaultHttpContext
            {
                RequestAborted = new CancellationToken(canceled: true),
            };
            context.Connection.RemoteIpAddress = IPAddress.Loopback;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                var task = (Task)executePowerAction!.Invoke(
                    service,
                    [context, LanRemoteAction.Sleep, ScheduledPowerActionType.Sleep])!;
                await task;
            });

            Assert.Contains(audit, entry => entry.Contains("authorization_rejected", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    [Fact]
    public async Task LanRemoteControlService_StartAsync_cleans_up_started_web_app_when_firewall_apply_fails()
    {
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        try
        {
            int port = GetFreeTcpPort();
            settings.Update(current =>
            {
                current.LanRemoteControl.Enabled = true;
                current.LanRemoteControl.Port = port;
            });

            var firewall = new ThrowingFirewall();
            using var cert = CreateSelfSignedLoopbackCertificate();
            using var service = new LanRemoteControlService(
                settings,
                CreateLanActions(),
                auth: new LanRemoteAuthStore(Path.Combine(root, "remote-control-auth.json")),
                firewall: firewall,
                addressProvider: () => [IPAddress.Loopback],
                portAvailable: (_, _) => true,
                certificateProvider: _ => new X509Certificate2(cert));

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync());

            Assert.False(service.GetState().Running);
            Assert.True(firewall.RemoveCount >= 1);
        }
        finally
        {
            DeleteDirectoryBestEffort(root);
        }
    }

    private static LanRemoteControlActions CreateLanActions()
        => new(
            () => new PowerPlan { PlanId = PlanId.Balanced, Name = "Balanced", IsActive = true },
            _ => true,
            _ => { },
            () => new WindowsPowerCapabilityState(SleepAvailable: true, HibernateAvailable: true),
            () => "1.0.0");

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static X509Certificate2 CreateSelfSignedLoopbackCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=VoltManager Coverage Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddHours(1));
        return new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.UserKeySet);
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of per-test temp directory.
        }
    }

    private sealed class NullLauncherEnvironment : ILauncherEnvironment
    {
        public string? ReadRegistryString(RegistryHive hive, string keyPath, string valueName) => null;
        public IEnumerable<UninstallEntry> GetUninstallEntries() => [];
        public bool FileExists(string path) => false;
        public string? ReadAllText(string path) => null;
        public string GetFolderPath(Environment.SpecialFolder folder) => string.Empty;
        public IEnumerable<string> FindFiles(string directory, string fileName, bool recursive) => [];
    }

    private sealed class NullBridgeDialogs : IBridgeFileDialogService
    {
        public Task<string?> SaveFileAsync(BridgeSaveFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> OpenFileAsync(BridgeOpenFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    private sealed class ThrowingFirewall : ILanRemoteFirewall
    {
        public int RemoveCount { get; private set; }
        public void Apply(int port) => throw new InvalidOperationException("synthetic firewall failure");
        public void Remove() => RemoveCount++;
    }
}
