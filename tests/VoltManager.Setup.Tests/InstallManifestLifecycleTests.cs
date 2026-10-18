using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class InstallManifestLifecycleTests
{
    [Fact]
    public void Staged_payload_with_modified_file_must_be_rejected_before_installing_any_bytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, "VoltManager");
        Directory.CreateDirectory(root);
        try
        {
            string staging = CreatePayload(root, "staging", "trusted version");
            string trustedZip = CreateArchive(root, staging, "trusted.zip");
            File.WriteAllText(Path.Combine(staging, "VoltManager.dll"), "untrusted modification");

            Assert.ThrowsAny<InvalidDataException>(() =>
                InstallEngine.ReplaceInstallDirectoryContents(
                    destination, staging, Path.Combine(root, "backup"), trustedZip));

            Assert.False(File.Exists(Path.Combine(destination, "VoltManager.dll")));
            Assert.Equal("untrusted modification", File.ReadAllText(Path.Combine(staging, "VoltManager.dll")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* synthetic fixture cleanup */ }
        }
    }

    [Fact]
    public void Synthetic_payload_supports_initial_install_same_inventory_update_repair_and_uninstall()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, "VoltManager");
        Directory.CreateDirectory(root);

        try
        {
            string first = CreatePayload(root, "first", "version one");
            string firstZip = CreateArchive(root, first, "first.zip");
            InstallEngine.ReplaceInstallDirectoryContents(
                destination, first, Path.Combine(root, "backup-first"), firstZip);

            Assert.Equal("version one", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            Assert.True(File.Exists(Path.Combine(destination, "wwwroot", "js", "app.js")));
            Assert.True(File.Exists(Path.Combine(destination, InstallManifest.FileName)));
            Assert.Contains("VoltManager.dll", InstallManifest.ReadOwnedEntries(destination));
            Assert.Contains("wwwroot", InstallManifest.ReadOwnedEntries(destination));
            Assert.Contains("runtimes", InstallManifest.ReadOwnedEntries(destination));

            // A new package cannot attest arbitrary previous-version bytes.
            // Same-inventory refresh is permitted; changed previous binaries
            // need an independently authenticated previous-release inventory.
            string updated = CreatePayload(root, "updated", "version one");
            string updatedZip = CreateArchive(root, updated, "updated.zip");
            InstallEngine.ReplaceInstallDirectoryContents(
                destination, updated, Path.Combine(root, "backup-updated"), updatedZip);

            Assert.Equal("version one", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            Assert.Equal("version one", File.ReadAllText(Path.Combine(destination, "wwwroot", "js", "app.js")));

            string repair = CreatePayload(root, "repair", "version one");
            string repairZip = CreateArchive(root, repair, "repair.zip");
            InstallEngine.ReplaceInstallDirectoryContents(
                destination, repair, Path.Combine(root, "backup-repair"), repairZip);

            Assert.Equal("version one", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            string unrelated = Path.Combine(destination, "wwwroot", "js", "private-notes.txt");
            File.WriteAllText(unrelated, "foreign fixture content");
            IReadOnlyList<string> refused = InstallManifest.DeleteOwnedEntries(destination, repairZip);
            Assert.NotEmpty(refused);
            Assert.Contains(refused, reason => reason.IndexOf("Unverified", StringComparison.OrdinalIgnoreCase) >= 0
                || reason.IndexOf("Unverifiable", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Equal("foreign fixture content", File.ReadAllText(unrelated));
            Assert.Equal("version one", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            Assert.True(File.Exists(Path.Combine(destination, InstallManifest.FileName)));

            // The unexpected file is synthetic and belongs to this test only.
            File.Delete(unrelated);
            Assert.Empty(InstallManifest.DeleteOwnedEntries(destination, repairZip));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* synthetic fixture cleanup */ }
        }
    }

    [Fact]
    public void Previous_manifest_with_file_removed_from_new_release_fails_closed_and_preserves_installation()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(root, "VoltManager");
        Directory.CreateDirectory(root);
        try
        {
            string initial = CreatePayload(root, "legacy", "legacy release");
            File.WriteAllText(Path.Combine(initial, "retired-library.dll"), "retired binary");
            string initialZip = CreateArchive(root, initial, "legacy.zip");
            InstallEngine.ReplaceInstallDirectoryContents(destination, initial,
                Path.Combine(root, "initial-backup"), initialZip);

            byte[] originalManifest = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
            string newer = CreatePayload(root, "new-release", "new release");
            string newerZip = CreateArchive(root, newer, "new-release.zip");
            string backup = Path.Combine(root, "update-backup");

            Assert.Throws<InvalidDataException>(() =>
                InstallEngine.ReplaceInstallDirectoryContents(destination, newer, backup, newerZip));

            Assert.Equal("retired binary", File.ReadAllText(Path.Combine(destination, "retired-library.dll")));
            Assert.Equal("legacy release", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            Assert.Equal(originalManifest, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
            Assert.False(Directory.Exists(backup));
            Assert.Equal("new release", File.ReadAllText(Path.Combine(newer, "VoltManager.dll")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* synthetic fixture cleanup */ }
        }
    }

    [Fact]
    public async Task Cross_version_update_verifies_installed_release_against_its_protected_uninstaller()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string destination, _) = InstallReleaseA(root);
            string notes = Path.Combine(destination, "user-notes.txt");
            File.WriteAllText(notes, "user data outside the payload");
            string zipB = CreateReleaseB(root);
            var guarded = new List<string>();

            string? warning = await ExtractAsync(destination, zipB, guarded.Add);

            Assert.Contains(destination, guarded);
            Assert.Equal("version B", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            Assert.Equal("version B", File.ReadAllText(Path.Combine(destination, "wwwroot", "js", "app.js")));
            Assert.True(File.Exists(Path.Combine(destination, "wwwroot", "js", "renamed-new.js")));
            Assert.False(File.Exists(Path.Combine(destination, "wwwroot", "js", "renamed-old.js")));
            Assert.False(File.Exists(Path.Combine(destination, "retired-library.dll")));
            Assert.False(Directory.Exists(Path.Combine(destination, "plugins")));
            Assert.False(File.Exists(Path.Combine(destination, "uninstall.exe")));
            Assert.Equal("user data outside the payload", File.ReadAllText(notes));
            Assert.DoesNotContain("retired-library.dll", InstallManifest.ReadOwnedEntries(destination));
            Assert.DoesNotContain("plugins", InstallManifest.ReadOwnedEntries(destination));

            string backup = Assert.Single(Directory.GetDirectories(root, ".VoltManager.backup-*"));
            Assert.Contains(backup, warning);
            Assert.Equal("retired in B", File.ReadAllText(Path.Combine(backup, "retired-library.dll")));
            Assert.True(File.Exists(Path.Combine(backup, "uninstall.exe")));
            Assert.Empty(Directory.GetDirectories(root, ".VoltManager.staging-*"));
            // L'immagine di A è stata letta solo come dati in un dominio poi scaricato.
            Assert.DoesNotContain(AppDomain.CurrentDomain.ReflectionOnlyGetAssemblies(),
                assembly => assembly.GetName().Name!.StartsWith("SyntheticSetup", StringComparison.Ordinal));
            Assert.Null(Record.Exception(() => InstallEngine.ReadEmbeddedPayloadWithoutExecution(
                File.ReadAllBytes(typeof(InstallManifest).Assembly.Location))));

            // Simula la registrazione dell'uninstaller B prima di verificarne la pulizia.
            File.Copy(typeof(InstallManifest).Assembly.Location, Path.Combine(destination, "uninstall.exe"));
            Assert.NotEmpty(InstallManifest.DeleteOwnedEntries(destination, zipB));
            Assert.Equal("user data outside the payload", File.ReadAllText(notes));
            Assert.Equal("version B", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            File.Delete(notes);
            Assert.Empty(InstallManifest.DeleteOwnedEntries(destination, zipB));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* pulizia della fixture sintetica */ }
        }
    }

    [Theory]
    [InlineData("not-an-image")]
    [InlineData("unprotected-forgery")]
    [InlineData("other-release")]
    [InlineData("foreign-file-in-owned-directory")]
    public async Task Cross_version_update_rejects_untrusted_installed_inventory_without_moving_anything(string attack)
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string destination, _) = InstallReleaseA(root);
            string uninstaller = Path.Combine(destination, "uninstall.exe");
            string dll = Path.Combine(destination, "VoltManager.dll");
            Action<string> guard = _ => { };
            switch (attack)
            {
                case "not-an-image":
                    File.WriteAllText(uninstaller, "not a setup image");
                    break;
                case "unprotected-forgery":
                    // Un inventario coerente ma non protetto non può autenticare byte manomessi.
                    File.WriteAllText(dll, "tampered");
                    string forged = CreateReleaseADirectory(root, "forged");
                    File.WriteAllText(Path.Combine(forged, "VoltManager.dll"), "tampered");
                    ReplaceUninstaller(root, destination, CreateArchive(root, forged, "forged.zip"));
                    guard = _ => throw new InvalidOperationException("synthetic user-writable uninstaller");
                    break;
                case "other-release":
                    ReplaceUninstaller(root, destination,
                        CreateArchive(root, CreatePayload(root, "release-c", "version C"), "c.zip"));
                    break;
                default:
                    File.WriteAllText(Path.Combine(destination, "wwwroot", "js", "private.txt"), "foreign");
                    break;
            }
            byte[] uninstallerBefore = File.ReadAllBytes(uninstaller);
            byte[] dllBefore = File.ReadAllBytes(dll);
            byte[] manifestBefore = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));

            await Assert.ThrowsAnyAsync<Exception>(() => ExtractAsync(destination, CreateReleaseB(root), guard));

            Assert.Equal(uninstallerBefore, File.ReadAllBytes(uninstaller));
            Assert.Equal(dllBefore, File.ReadAllBytes(dll));
            Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
            Assert.True(File.Exists(Path.Combine(destination, "retired-library.dll")));
            Assert.True(File.Exists(Path.Combine(destination, "plugins", "legacy.dll")));
            Assert.Empty(Directory.GetDirectories(root, ".VoltManager.backup-*"));
            if (attack == "foreign-file-in-owned-directory")
                Assert.Equal("foreign", File.ReadAllText(Path.Combine(destination, "wwwroot", "js", "private.txt")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* pulizia della fixture sintetica */ }
        }
    }

    [Fact]
    public void Cross_version_swap_failure_restores_release_A_including_its_uninstaller()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManagerSetupTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (string destination, _) = InstallReleaseA(root);
            byte[] uninstallerBefore = File.ReadAllBytes(Path.Combine(destination, "uninstall.exe"));
            byte[] manifestBefore = File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName));
            string staging = CreatePayload(root, "release-b-staging", "version B");
            File.WriteAllText(Path.Combine(staging, "wwwroot", "js", "renamed-new.js"), "renamed in B");
            string zipB = CreateArchive(root, staging, "b-staging.zip");
            string backup = Path.Combine(root, "rollback-backup");
            Action<string, string> move = (source, target) =>
            {
                if (string.Equals(source, Path.Combine(staging, "wwwroot"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("synthetic locked entry");
                if (File.Exists(source)) File.Move(source, target);
                else Directory.Move(source, target);
            };

            Assert.Throws<IOException>(() => InstallEngine.ReplaceInstallDirectoryContentsCore(
                destination, staging, backup, zipB, move, _ => { }));

            Assert.Equal("version A", File.ReadAllText(Path.Combine(destination, "VoltManager.dll")));
            Assert.Equal("retired in B", File.ReadAllText(Path.Combine(destination, "retired-library.dll")));
            Assert.True(File.Exists(Path.Combine(destination, "plugins", "legacy.dll")));
            Assert.True(File.Exists(Path.Combine(destination, "wwwroot", "js", "renamed-old.js")));
            Assert.Equal(uninstallerBefore, File.ReadAllBytes(Path.Combine(destination, "uninstall.exe")));
            Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(destination, InstallManifest.FileName)));
            Assert.Equal("version B", File.ReadAllText(Path.Combine(staging, "VoltManager.dll")));
            Assert.False(Directory.Exists(backup));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* pulizia della fixture sintetica */ }
        }
    }

    // A è installata dallo swap reale; il suo setup sintetico incorpora il relativo ZIP.
    internal static (string Destination, string ZipA) InstallReleaseA(string root)
    {
        string destination = Path.Combine(root, "VoltManager");
        string a = CreateReleaseADirectory(root, "release-a");
        string zipA = CreateArchive(root, a, "a.zip");
        InstallEngine.ReplaceInstallDirectoryContents(destination, a, Path.Combine(root, "backup-a"), zipA);
        ReplaceUninstaller(root, destination, zipA);
        return (destination, zipA);
    }

    private static string CreateReleaseADirectory(string root, string name)
    {
        string a = CreatePayload(root, name, "version A");
        File.WriteAllText(Path.Combine(a, "retired-library.dll"), "retired in B");
        File.WriteAllText(Path.Combine(a, "wwwroot", "js", "renamed-old.js"), "renamed in B");
        Directory.CreateDirectory(Path.Combine(a, "plugins"));
        File.WriteAllText(Path.Combine(a, "plugins", "legacy.dll"), "plugin only in A");
        return a;
    }

    internal static string CreateReleaseB(string root)
    {
        string b = CreatePayload(root, "release-b-" + Guid.NewGuid().ToString("N"), "version B");
        File.WriteAllText(Path.Combine(b, "wwwroot", "js", "renamed-new.js"), "renamed in B");
        return CreateArchive(root, b, Path.GetFileName(b) + ".zip");
    }

    private static void ReplaceUninstaller(string root, string destination, string payloadZip)
    {
        string directory = Path.Combine(root, "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var name = new AssemblyName("SyntheticSetup" + Guid.NewGuid().ToString("N"));
        AssemblyBuilder assembly = AppDomain.CurrentDomain.DefineDynamicAssembly(name, AssemblyBuilderAccess.Save, directory);
        ModuleBuilder module = assembly.DefineDynamicModule(name.Name!, "setup.exe");
        using (FileStream payload = File.OpenRead(payloadZip))
        {
            module.DefineManifestResource("VoltManager.Setup.Payload.payload.zip", payload, ResourceAttributes.Public);
            assembly.Save("setup.exe");
        }
        File.Copy(Path.Combine(directory, "setup.exe"), Path.Combine(destination, "uninstall.exe"), overwrite: true);
    }

    private static async Task<string?> ExtractAsync(string destination, string zip, Action<string> guard)
    {
        MethodInfo extract = typeof(InstallEngine).GetMethod(
            "ExtractPayloadAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        return await (Task<string?>)extract.Invoke(null,
            new object?[] { destination, CancellationToken.None, zip, guard })!;
    }

    internal static string CreatePayload(string root, string name, string content)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "wwwroot", "js"));
        Directory.CreateDirectory(Path.Combine(directory, "runtimes", "win-x64", "native"));
        File.WriteAllText(Path.Combine(directory, "VoltManager.exe"), content);
        File.WriteAllText(Path.Combine(directory, "VoltManager.dll"), content);
        File.WriteAllText(Path.Combine(directory, "VoltManager.exe.config"), "<configuration />");
        File.WriteAllText(Path.Combine(directory, "wwwroot", "index.html"), "<html>VoltManager</html>");
        File.WriteAllText(Path.Combine(directory, "wwwroot", "js", "app.js"), content);
        File.WriteAllText(Path.Combine(directory, "runtimes", "win-x64", "native", "helper.dll"), content);
        return directory;
    }

    internal static string CreateArchive(string root, string payloadDir, string archiveName)
    {
        string archive = Path.Combine(root, archiveName);
        ZipFile.CreateFromDirectory(payloadDir, archive);
        return archive;
    }
}
