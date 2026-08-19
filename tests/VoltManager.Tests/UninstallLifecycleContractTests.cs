using System.Reflection;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class UninstallLifecycleContractTests
{
    private static Type RequireArtifactsType()
    {
        Type? type = typeof(SettingsService).Assembly.GetType("VoltManager.Services.VoltManagerArtifacts");
        Assert.NotNull(type);
        return type!;
    }

    [Fact]
    public void Artifact_manifest_covers_every_owned_process_and_known_temp_file()
    {
        Type type = RequireArtifactsType();

        string[] processNames = Assert.IsType<string[]>(
            type.GetProperty("ProcessNames", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        string[] tempNames = Assert.IsType<string[]>(
            type.GetProperty("TempArtifactFileNames", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));

        Assert.Contains("VoltManager", processNames);
        Assert.Contains("VoltManager.Supervisor", processNames);
        Assert.Contains("VoltManager.HardwareService", processNames);
        Assert.Contains("VoltManagerPlanSwitch", processNames);

        Assert.Contains("VoltManagerUninstall.exe", tempNames);
        Assert.Contains("VoltManagerUpdate.exe", tempNames);
        Assert.Contains("VoltManagerPayload.zip", tempNames);
        Assert.Contains("MicrosoftEdgeWebview2Setup.exe", tempNames);
        Assert.Contains("vmgr_update_cleanup.bat", tempNames);
        Assert.Contains("vmgr_uninstall_cleanup.bat", tempNames);
    }

    [Fact]
    public void Temp_cleanup_removes_owned_artifacts_but_preserves_unrelated_files_and_current_uninstaller()
    {
        Type type = RequireArtifactsType();
        MethodInfo? method = type.GetMethod(
            "CleanupOwnedTempArtifacts",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(string) },
            modifiers: null);
        Assert.NotNull(method);

        string root = Path.Combine(Path.GetTempPath(), "VoltManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string update = Path.Combine(root, "VoltManagerUpdate.exe");
            string payload = Path.Combine(root, "VoltManagerPayload.zip");
            string updateCleanup = Path.Combine(root, "vmgr_update_cleanup.bat");
            string uninstallCleanup = Path.Combine(root, "vmgr_uninstall_cleanup.bat");
            string unrelated = Path.Combine(root, "keep-me.txt");
            string currentUninstaller = Path.Combine(root, "VoltManagerUninstall.exe");
            File.WriteAllText(update, "stale");
            File.WriteAllText(payload, "stale");
            File.WriteAllText(updateCleanup, "stale");
            File.WriteAllText(uninstallCleanup, "stale");
            File.WriteAllText(unrelated, "user-owned");
            File.WriteAllText(currentUninstaller, "running-self-placeholder");

            object? result = method!.Invoke(null, new object?[] { root, currentUninstaller });
            Assert.NotNull(result);

            Assert.False(File.Exists(update));
            Assert.False(File.Exists(payload));
            Assert.False(File.Exists(updateCleanup));
            Assert.False(File.Exists(uninstallCleanup));
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(currentUninstaller));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Shutdown_signal_wakes_an_existing_application_listener()
    {
        Type type = RequireArtifactsType();
        string eventName = Assert.IsType<string>(
            type.GetProperty("UninstallShutdownEventName", BindingFlags.Public | BindingFlags.Static)!.GetValue(null));
        MethodInfo? method = type.GetMethod("TrySignalApplicationShutdown", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        bool signaled = Assert.IsType<bool>(method!.Invoke(null, null));
        Assert.True(signaled);
        Assert.True(shutdownEvent.WaitOne(TimeSpan.FromSeconds(1)));
    }
}
