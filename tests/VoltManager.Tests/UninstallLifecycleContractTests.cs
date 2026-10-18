using System.IO;
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
        // The WebView2 installer is staged in a unique subdirectory, so a root-level
        // file with this generic name is not an owned VoltManager artifact.
        Assert.DoesNotContain("MicrosoftEdgeWebview2Setup.exe", tempNames);
        Assert.Contains("vmgr_update_cleanup.bat", tempNames);
        Assert.Contains("vmgr_uninstall_cleanup.bat", tempNames);
    }

    [Fact]
    public void Temp_cleanup_reports_ambiguous_legacy_artifacts_without_removing_any_files()
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
            string unrelatedWebViewBootstrapper = Path.Combine(root, "MicrosoftEdgeWebview2Setup.exe");
            string currentUninstaller = Path.Combine(root, "VoltManagerUninstall.exe");
            File.WriteAllText(update, "stale");
            File.WriteAllText(payload, "stale");
            File.WriteAllText(updateCleanup, "stale");
            File.WriteAllText(uninstallCleanup, "stale");
            File.WriteAllText(unrelated, "user-owned");
            File.WriteAllText(unrelatedWebViewBootstrapper, "unrelated bootstrapper");
            File.WriteAllText(currentUninstaller, "running-self-placeholder");

            object? result = method!.Invoke(null, new object?[] { root, currentUninstaller });
            Assert.NotNull(result);

            string[] failures = Assert.IsType<string[]>(result);
            Assert.Equal("stale", File.ReadAllText(update));
            Assert.Equal("stale", File.ReadAllText(payload));
            Assert.Equal("stale", File.ReadAllText(updateCleanup));
            Assert.Equal("stale", File.ReadAllText(uninstallCleanup));
            Assert.Contains(failures, failure => failure.Contains("VoltManagerUpdate.exe") && failure.Contains("unverified"));
            Assert.Contains(failures, failure => failure.Contains("VoltManagerPayload.zip") && failure.Contains("unverified"));
            Assert.Contains(failures, failure => failure.Contains("vmgr_update_cleanup.bat") && failure.Contains("unverified"));
            Assert.Contains(failures, failure => failure.Contains("vmgr_uninstall_cleanup.bat") && failure.Contains("unverified"));
            Assert.True(File.Exists(unrelated));
            Assert.Equal("unrelated bootstrapper", File.ReadAllText(unrelatedWebViewBootstrapper));
            Assert.True(File.Exists(currentUninstaller));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Temp_cleanup_never_deletes_unverified_foreign_files_with_reserved_names()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string[] names =
        {
            "VoltManagerUpdate.exe", "VoltManagerUninstall.exe", "VoltManagerPayload.zip",
            "VoltManagerPreviewSetup.exe", "VoltManagerPreviewPayload.zip",
            "vmgr_update_cleanup.bat", "vmgr_uninstall_cleanup.bat",
        };
        var before = new Dictionary<string, byte[]>();
        var attributes = new Dictionary<string, FileAttributes>();

        try
        {
            for (int index = 0; index < names.Length; index++)
            {
                string path = Path.Combine(root, names[index]);
                byte[] content = { 0, 255, (byte)index, 17, 29 };
                File.WriteAllBytes(path, content);
                if (index == 0)
                    File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                before.Add(path, content);
                attributes.Add(path, File.GetAttributes(path));
            }

            string current = Path.Combine(root, "current-uninstaller.exe");
            File.WriteAllText(current, "keep running process image");
            string[] failures = VoltManagerArtifacts.CleanupOwnedTempArtifacts(root, current);

            foreach (var entry in before)
            {
                Assert.True(File.Exists(entry.Key), "Unverified file removed: " + entry.Key);
                Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key));
                Assert.Equal(attributes[entry.Key], File.GetAttributes(entry.Key));
            }
            Assert.Equal("keep running process image", File.ReadAllText(current));
            Assert.All(names, name => Assert.Contains(failures, failure => failure.Contains(name)));
            Assert.All(failures, failure => Assert.Contains("unverified", failure, StringComparison.OrdinalIgnoreCase));
            string[] remaining = VoltManagerArtifacts.FindOwnedTempArtifacts(root, current);
            Assert.All(names, name => Assert.Contains(remaining, path =>
                string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            // Synthetic directory created by this test only; restore own fixture attributes.
            foreach (string name in names)
            {
                string path = Path.Combine(root, name);
                try { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); } catch { }
            }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Directory_cleanup_must_not_recursively_delete_unverified_synthetic_updates_tree()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerTests", Guid.NewGuid().ToString("N"));
        string updates = Path.Combine(root, "Updates");
        string privateDir = Path.Combine(updates, "private", "nested");
        Directory.CreateDirectory(privateDir);
        string sentinel = Path.Combine(privateDir, "document.bin");
        File.WriteAllBytes(sentinel, new byte[] { 45, 33, 255, 0, 7 });

        try
        {
            MethodInfo delete = RequireArtifactsType().GetMethod("TryDeleteDirectoryTree",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert.NotNull(delete);
            object?[] args = { updates, string.Empty };
            bool success = Assert.IsType<bool>(delete.Invoke(null, args));
            Assert.False(success);
            Assert.Contains("unverified", Assert.IsType<string>(args[1]), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new byte[] { 45, 33, 255, 0, 7 }, File.ReadAllBytes(sentinel));
            Assert.True(Directory.Exists(privateDir));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Shutdown_signal_wakes_an_existing_application_listener()
    {
        Type type = RequireArtifactsType();
        // Never signal the installed app's event: its listener would consume the signal and exit.
        string eventName = "VoltManagerTests_Shutdown_" + Guid.NewGuid().ToString("N");
        MethodInfo? method = type.GetMethod("TrySignalShutdownEvent", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);

        bool signaled = Assert.IsType<bool>(method!.Invoke(null, new object[] { eventName }));
        Assert.True(signaled);
        Assert.True(shutdownEvent.WaitOne(TimeSpan.FromSeconds(1)));
    }
}
