#pragma warning disable CA1416
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public sealed class StartupAppsServiceTests
{
    [Fact]
    public void GetStartupApps_Includes_Hkcu_And_Hklm_64And32Bit_Entries()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.HkcuRun.SetValue("HkcuApp", "\"C:\\test\\hkcu.exe\"");
        fixture.Hklm64Run.SetValue("Hklm64App", "\"C:\\test\\hklm64.exe\"");
        fixture.Hklm32Run.SetValue("Hklm32App", "\"C:\\test\\hklm32.exe\"");

        File.WriteAllText(Path.Combine(fixture.UserStartupFolder, "UserApp.lnk"), "test");
        File.WriteAllText(Path.Combine(fixture.CommonStartupFolder, "CommonApp.lnk"), "test");

        using var svc = fixture.CreateService();
        var snapshot = svc.GetStartupApps();
        var all = snapshot.Enabled.Concat(snapshot.Disabled).ToList();

        Assert.Equal(5, all.Count);

        var hkcu = all.Single(e => e.Name == "HkcuApp");
        Assert.Equal("HKCU Run", hkcu.Source);
        Assert.Equal("HKCU", hkcu.Hive);
        Assert.Equal("Shared", hkcu.View);
        Assert.Equal("Shared", hkcu.RegistryView);
        Assert.Equal("HKCU Run:HkcuApp", hkcu.Id);
        Assert.False(hkcu.IsManaged);

        var hklm64 = all.Single(e => e.Name == "Hklm64App");
        Assert.Equal("HKLM Run (64-bit)", hklm64.Source);
        Assert.Equal("HKLM", hklm64.Hive);
        Assert.Equal("Registry64", hklm64.View);
        Assert.Equal("Registry64", hklm64.RegistryView);
        Assert.Equal("HKLM Run (64-bit):Hklm64App", hklm64.Id);

        var hklm32 = all.Single(e => e.Name == "Hklm32App");
        Assert.Equal("HKLM Run (32-bit)", hklm32.Source);
        Assert.Equal("HKLM", hklm32.Hive);
        Assert.Equal("Registry32", hklm32.View);
        Assert.Equal("Registry32", hklm32.RegistryView);
        Assert.Equal("HKLM Run (32-bit):Hklm32App", hklm32.Id);

        var userFolder = all.Single(e => e.Name == "UserApp");
        Assert.Equal("Startup folder", userFolder.Source);
        Assert.Equal("HKCU", userFolder.Hive);
        Assert.Null(userFolder.View);
        Assert.Null(userFolder.RegistryView);
        Assert.Equal("Startup folder:UserApp.lnk", userFolder.Id);

        var commonFolder = all.Single(e => e.Name == "CommonApp");
        Assert.Equal("Common startup folder", commonFolder.Source);
        Assert.Equal("HKLM", commonFolder.Hive);
        Assert.Null(commonFolder.View);
        Assert.Null(commonFolder.RegistryView);
        Assert.Equal("Common startup folder:CommonApp.lnk", commonFolder.Id);
    }

    [Fact]
    public void StartupAppEntry_Serializes_Both_View_And_RegistryView_Properties()
    {
        var entry = new StartupAppEntry
        {
            Id = "test-id",
            Name = "TestApp",
            Command = "C:\\test\\app.exe",
            Path = "C:\\test\\app.exe",
            Source = "HKLM Run (64-bit)",
            Enabled = true,
            IsManaged = false,
            Hive = "HKLM",
            View = "Registry64",
            StateKnown = true,
        };

        string json = JsonSerializer.Serialize(entry);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("view", out var viewProp));
        Assert.Equal("Registry64", viewProp.GetString());

        Assert.True(root.TryGetProperty("registryView", out var regViewProp));
        Assert.Equal("Registry64", regViewProp.GetString());
    }

    [Fact]
    public void GetStartupApps_Collision_Same_Name_In_Multiple_Locations_Produces_Unique_Ids_And_No_Loss()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        const string name = "SharedName";

        fixture.HkcuRun.SetValue(name, "\"C:\\test\\hkcu.exe\"");
        fixture.Hklm64Run.SetValue(name, "\"C:\\test\\hklm64.exe\"");
        fixture.Hklm32Run.SetValue(name, "\"C:\\test\\hklm32.exe\"");

        File.WriteAllText(Path.Combine(fixture.UserStartupFolder, $"{name}.lnk"), "user");
        File.WriteAllText(Path.Combine(fixture.CommonStartupFolder, $"{name}.lnk"), "common");

        using var svc = fixture.CreateService();
        var snapshot = svc.GetStartupApps();
        var all = snapshot.Enabled.Concat(snapshot.Disabled).ToList();

        // All 5 entries must be preserved despite sharing the same base name
        Assert.Equal(5, all.Count);
        var ids = all.Select(e => e.Id).ToList();
        Assert.Equal(5, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Contains("HKCU Run:SharedName", ids);
        Assert.Contains("HKLM Run (64-bit):SharedName", ids);
        Assert.Contains("HKLM Run (32-bit):SharedName", ids);
        Assert.Contains("Startup folder:SharedName.lnk", ids);
        Assert.Contains("Common startup folder:SharedName.lnk", ids);
    }

    [Fact]
    public void GetStartupApps_Approval_State_Decodes_Enabled_And_Disabled_Correctly()
    {
        using var fixture = new IsolatedStartupRegistryFixture();

        // 1. Explicitly enabled with 0x02
        fixture.HkcuRun.SetValue("App02", "\"C:\\test\\app02.exe\"");
        fixture.SetHkcuApproval("Run", "App02", new byte[] { 0x02, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });

        // 2. Explicitly enabled with 0x06
        fixture.HkcuRun.SetValue("App06", "\"C:\\test\\app06.exe\"");
        fixture.SetHkcuApproval("Run", "App06", new byte[] { 0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // 3. Explicitly disabled with 0x03
        fixture.HkcuRun.SetValue("App03", "\"C:\\test\\app03.exe\"");
        fixture.SetHkcuApproval("Run", "App03", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // 4. Explicitly disabled with 0x07
        fixture.HkcuRun.SetValue("App07", "\"C:\\test\\app07.exe\"");
        fixture.SetHkcuApproval("Run", "App07", new byte[] { 0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // 5. No StartupApproved entry (default enabled, stateKnown = false)
        fixture.HkcuRun.SetValue("AppDefault", "\"C:\\test\\default.exe\"");

        // 6. Corrupt entry (0x00 byte -> default enabled, stateKnown = false)
        fixture.HkcuRun.SetValue("AppCorrupt", "\"C:\\test\\corrupt.exe\"");
        fixture.SetHkcuApproval("Run", "AppCorrupt", new byte[] { 0x00, 0x00, 0x00, 0x00 });

        using var svc = fixture.CreateService();
        var snapshot = svc.GetStartupApps();

        var app02 = snapshot.Enabled.Single(e => e.Name == "App02");
        Assert.True(app02.Enabled);
        Assert.True(app02.StateKnown);

        var app06 = snapshot.Enabled.Single(e => e.Name == "App06");
        Assert.True(app06.Enabled);
        Assert.True(app06.StateKnown);

        var app03 = snapshot.Disabled.Single(e => e.Name == "App03");
        Assert.False(app03.Enabled);
        Assert.True(app03.StateKnown);

        var app07 = snapshot.Disabled.Single(e => e.Name == "App07");
        Assert.False(app07.Enabled);
        Assert.True(app07.StateKnown);

        var appDefault = snapshot.Enabled.Single(e => e.Name == "AppDefault");
        Assert.True(appDefault.Enabled);
        Assert.False(appDefault.StateKnown);

        var appCorrupt = snapshot.Enabled.Single(e => e.Name == "AppCorrupt");
        Assert.True(appCorrupt.Enabled);
        Assert.False(appCorrupt.StateKnown);
    }

    [Fact]
    public void GetStartupApps_Hklm_Honors_Run_For_64Bit_And_Run32_For_32Bit()
    {
        using var fixture = new IsolatedStartupRegistryFixture();

        // 64-bit app disabled in HKLM Run
        fixture.Hklm64Run.SetValue("App64", "\"C:\\test\\app64.exe\"");
        fixture.SetHklmApproval("Run", "App64", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        // 32-bit app disabled in HKLM Run32 (even if Run is enabled or absent)
        fixture.Hklm32Run.SetValue("App32", "\"C:\\test\\app32.exe\"");
        fixture.SetHklmApproval("Run32", "App32", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        fixture.SetHklmApproval("Run", "App32", new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        using var svc = fixture.CreateService();
        var snapshot = svc.GetStartupApps();

        var app64 = snapshot.Disabled.Single(e => e.Name == "App64");
        Assert.False(app64.Enabled);
        Assert.True(app64.StateKnown);

        var app32 = snapshot.Disabled.Single(e => e.Name == "App32");
        Assert.False(app32.Enabled);
        Assert.True(app32.StateKnown);
    }

    [Fact]
    public void GetStartupApps_Hkcu_User_Override_Overrides_Hklm()
    {
        using var fixture = new IsolatedStartupRegistryFixture();

        // HKLM says enabled (0x02), but user disabled it in HKCU (0x03)
        fixture.Hklm64Run.SetValue("OverriddenApp", "\"C:\\test\\app.exe\"");
        fixture.SetHklmApproval("Run", "OverriddenApp", new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        fixture.SetHkcuApproval("Run", "OverriddenApp", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        using var svc = fixture.CreateService();
        var snapshot = svc.GetStartupApps();

        var app = snapshot.Disabled.Single(e => e.Name == "OverriddenApp");
        Assert.False(app.Enabled);
        Assert.True(app.StateKnown);
    }

    [Fact]
    public void SetStartupAppEnabled_Toggles_Hkcu_And_Verifies()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.HkcuRun.SetValue("TogglableApp", "\"C:\\test\\toggle.exe\"");

        using var svc = fixture.CreateService();

        // Initially default enabled
        var init = svc.GetStartupApps().Enabled.Single(e => e.Name == "TogglableApp");
        Assert.True(init.Enabled);

        // Disable
        bool resultDisable = svc.SetStartupAppEnabled(init.Id, false);
        Assert.True(resultDisable);

        var readDisabled = fixture.GetHkcuApproval("Run", "TogglableApp");
        Assert.NotNull(readDisabled);
        Assert.Equal(0x03, readDisabled[0]);

        // Re-read snapshot
        var disabledSnapshot = svc.GetStartupApps();
        var disabledEntry = disabledSnapshot.Disabled.Single(e => e.Name == "TogglableApp");
        Assert.False(disabledEntry.Enabled);
        Assert.True(disabledEntry.StateKnown);

        // Re-enable
        bool resultEnable = svc.SetStartupAppEnabled(init.Id, true);
        Assert.True(resultEnable);

        var readEnabled = fixture.GetHkcuApproval("Run", "TogglableApp");
        Assert.NotNull(readEnabled);
        Assert.Equal(0x02, readEnabled[0]);

        var enabledSnapshot = svc.GetStartupApps();
        var enabledEntry = enabledSnapshot.Enabled.Single(e => e.Name == "TogglableApp");
        Assert.True(enabledEntry.Enabled);
        Assert.True(enabledEntry.StateKnown);
    }

    [Fact]
    public void SetStartupAppEnabled_32Bit_Writes_Run32_And_64Bit_Writes_Run()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.Hklm64Run.SetValue("App64", "\"C:\\test\\app64.exe\"");
        fixture.Hklm32Run.SetValue("App32", "\"C:\\test\\app32.exe\"");

        using var svc = fixture.CreateService();

        svc.SetStartupAppEnabled("HKLM Run (64-bit):App64", false);
        svc.SetStartupAppEnabled("HKLM Run (32-bit):App32", false);

        var app64Approval = fixture.GetHklmApproval("Run", "App64");
        Assert.NotNull(app64Approval);
        Assert.Equal(0x03, app64Approval[0]);

        var app32Approval = fixture.GetHklmApproval("Run32", "App32");
        Assert.NotNull(app32Approval);
        Assert.Equal(0x03, app32Approval[0]);
    }

    [Fact]
    public void SetStartupAppEnabled_NonElevated_Hklm_FallsBack_To_Hkcu_Override()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.Hklm64Run.SetValue("MachineApp", "\"C:\\test\\machine.exe\"");

        // Open a read-only view of HKLM root to simulate non-elevated user permissions
        using var readOnlyHklm = fixture.TestRoot.OpenSubKey("HKLM64", writable: false)!;
        using var svc = new StartupAppsService(
            fixture.HkcuRoot,
            readOnlyHklm,
            null,
            fixture.UserStartupFolder,
            fixture.CommonStartupFolder,
            ownsRoots: false);

        // Disabling an HKLM app without admin rights must succeed by writing the override to HKCU
        bool disabled = svc.SetStartupAppEnabled("HKLM Run:MachineApp", false);
        Assert.True(disabled);

        var hkcuApproval = fixture.GetHkcuApproval("Run", "MachineApp");
        Assert.NotNull(hkcuApproval);
        Assert.Equal(0x03, hkcuApproval[0]);
        Assert.Null(fixture.GetHkcuApproval("Run32", "MachineApp"));
        Assert.Contains(svc.GetStartupApps().Disabled, entry => entry.Name == "MachineApp");
        Assert.True(svc.SetStartupAppEnabled("HKLM Run:MachineApp", true));
        Assert.Contains(svc.GetStartupApps().Enabled, entry => entry.Name == "MachineApp");
        Assert.Equal(0x02, fixture.GetHkcuApproval("Run", "MachineApp")![0]);
        Assert.Null(fixture.GetHklmApproval("Run", "MachineApp"));
    }

    [Fact]
    public void SetStartupAppEnabled_NonElevated_Hklm32_FallsBack_To_Hkcu_Run32_Override()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.Hklm32Run.SetValue("Machine32App", "\"C:\\test\\machine32.exe\"");

        // Open read-only view of HKLM root
        using var readOnlyHklm = fixture.TestRoot.OpenSubKey("HKLM64", writable: false)!;
        using var readOnlyHklm32 = fixture.TestRoot.OpenSubKey("HKLM32", writable: false)!;
        using var svc = new StartupAppsService(
            fixture.HkcuRoot,
            readOnlyHklm,
            readOnlyHklm32,
            fixture.UserStartupFolder,
            fixture.CommonStartupFolder,
            ownsRoots: false);

        bool disabled = svc.SetStartupAppEnabled("HKLM Run (32-bit):Machine32App", false);
        Assert.True(disabled);

        // Verification: HKCU Run32 key should have the override
        var hkcuApproval = fixture.GetHkcuApproval("Run32", "Machine32App");
        Assert.NotNull(hkcuApproval);
        Assert.Equal(0x03, hkcuApproval[0]);

        var snapshot = svc.GetStartupApps();
        var entry = snapshot.Disabled.Single(e => e.Name == "Machine32App");
        Assert.False(entry.Enabled);
        Assert.True(entry.StateKnown);
    }

    [Fact]
    public void SetStartupAppEnabled_Syncs_Hkcu_Override_If_Present()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.Hklm64Run.SetValue("SyncedApp", "\"C:\\test\\synced.exe\"");

        // User previously had disabled it in HKCU
        fixture.SetHkcuApproval("Run", "SyncedApp", new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        using var svc = fixture.CreateService();
        // Admin enables it
        svc.SetStartupAppEnabled("HKLM Run (64-bit):SyncedApp", true);

        // Both HKLM and HKCU should now be enabled
        var hklm = fixture.GetHklmApproval("Run", "SyncedApp");
        Assert.NotNull(hklm);
        Assert.Equal(0x02, hklm[0]);

        var hkcu = fixture.GetHkcuApproval("Run", "SyncedApp");
        Assert.NotNull(hkcu);
        Assert.Equal(0x02, hkcu[0]);
    }

    [Fact]
    public void SetStartupAppEnabled_Throws_When_Id_Not_Found()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        using var svc = fixture.CreateService();

        Assert.Throws<InvalidOperationException>(() => svc.SetStartupAppEnabled("NonExistent:App", false));
    }

    [Fact]
    public void SetStartupAppEnabled_Throws_When_Id_Is_Empty()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        using var svc = fixture.CreateService();

        Assert.Throws<ArgumentException>(() => svc.SetStartupAppEnabled("", false));
        Assert.Throws<ArgumentException>(() => svc.SetStartupAppEnabled("   ", false));
    }

    [Fact]
    public void SetStartupAppEnabled_Supports_Legacy_Hklm_Id_Lookup()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.Hklm64Run.SetValue("LegacyApp", "\"C:\\test\\legacy.exe\"");

        using var svc = fixture.CreateService();

        // Old UI or old callers pass "HKLM Run:LegacyApp"
        bool changed = svc.SetStartupAppEnabled("HKLM Run:LegacyApp", false);
        Assert.True(changed);

        var val = fixture.GetHklmApproval("Run", "LegacyApp");
        Assert.NotNull(val);
        Assert.Equal(0x03, val[0]);
    }

    [Fact]
    public void SetStartupAppEnabled_Propagates_Write_Errors_When_Hkcu_Not_Writable()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        fixture.HkcuRun.SetValue("ReadOnlyApp", "\"C:\\test\\readonly.exe\"");

        // Open a read-only view of the HKCU root
        using var readOnlyHkcu = fixture.TestRoot.OpenSubKey("HKCU", writable: false)!;
        using var svc = new StartupAppsService(
            readOnlyHkcu,
            fixture.Hklm64Root,
            fixture.Hklm32Root,
            fixture.UserStartupFolder,
            fixture.CommonStartupFolder,
            ownsRoots: false);

        // Writing HKCU item to read-only HKCU root must fail and throw UnauthorizedAccessException
        Assert.Throws<UnauthorizedAccessException>(() =>
            svc.SetStartupAppEnabled("HKCU Run:ReadOnlyApp", false));
    }

    [Fact]
    public void ManagedApp_Add_And_Remove_Maintains_Backward_Compatibility()
    {
        using var fixture = new IsolatedStartupRegistryFixture();
        string tempExe = Path.Combine(fixture.UserStartupFolder, "MyManagedTool.exe");
        File.WriteAllText(tempExe, "test executable");

        using var svc = fixture.CreateService();

        var entry = svc.AddManagedStartupApp(tempExe);
        Assert.NotNull(entry);
        Assert.StartsWith("Miliano's App - MyManagedTool", entry.Id, StringComparison.Ordinal);
        Assert.Equal(entry.Id, entry.Name);
        Assert.True(entry.IsManaged);
        Assert.True(entry.Enabled);
        Assert.True(entry.StateKnown);
        Assert.Equal("HKCU Run", entry.Source);
        Assert.Equal("HKCU", entry.Hive);

        // Verify in snapshot
        var snapshot = svc.GetStartupApps();
        var managedInSnapshot = snapshot.Enabled.Single(e => e.Id == entry.Id);
        Assert.True(managedInSnapshot.IsManaged);

        // Toggle enabled on managed app
        svc.SetStartupAppEnabled(entry.Id, false);
        var disabledSnapshot = svc.GetStartupApps();
        Assert.Contains(disabledSnapshot.Disabled, e => e.Id == entry.Id);

        // Remove managed app
        bool removed = svc.RemoveManagedStartupApp(entry.Id);
        Assert.True(removed);

        var finalSnapshot = svc.GetStartupApps();
        Assert.DoesNotContain(finalSnapshot.Enabled.Concat(finalSnapshot.Disabled), e => e.Id == entry.Id);

        // Cannot remove non-managed app
        Assert.Throws<InvalidOperationException>(() => svc.RemoveManagedStartupApp("UnmanagedApp"));
    }

    [Fact]
    public void RealSystem_DefaultConstructor_GetStartupApps_Is_ReadOnly_And_Succeeds()
    {
        using var svc = new StartupAppsService();
        var snapshot = svc.GetStartupApps();

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.Enabled);
        Assert.NotNull(snapshot.Disabled);
    }

    private sealed class IsolatedStartupRegistryFixture : IDisposable
    {
        private readonly string _testKeyPath;
        public RegistryKey TestRoot { get; }
        public RegistryKey HkcuRoot { get; }
        public RegistryKey Hklm64Root { get; }
        public RegistryKey Hklm32Root { get; }
        public RegistryKey HkcuRun { get; }
        public RegistryKey Hklm64Run { get; }
        public RegistryKey Hklm32Run { get; }
        public string UserStartupFolder { get; }
        public string CommonStartupFolder { get; }

        public IsolatedStartupRegistryFixture()
        {
            _testKeyPath = @"Software\VoltManager_Tests_" + Guid.NewGuid().ToString("N");
            TestRoot = Registry.CurrentUser.CreateSubKey(_testKeyPath, writable: true)
                ?? throw new InvalidOperationException("Impossibile creare la chiave di test isolata.");

            HkcuRoot = TestRoot.CreateSubKey("HKCU", writable: true)!;
            Hklm64Root = TestRoot.CreateSubKey("HKLM64", writable: true)!;
            Hklm32Root = TestRoot.CreateSubKey("HKLM32", writable: true)!;

            HkcuRun = HkcuRoot.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
            Hklm64Run = Hklm64Root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
            Hklm32Run = Hklm32Root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)!;

            string tempBase = Path.Combine(Path.GetTempPath(), "VM_StartupTests_" + Guid.NewGuid().ToString("N"));
            UserStartupFolder = Path.Combine(tempBase, "UserStartup");
            CommonStartupFolder = Path.Combine(tempBase, "CommonStartup");
            Directory.CreateDirectory(UserStartupFolder);
            Directory.CreateDirectory(CommonStartupFolder);
        }

        public StartupAppsService CreateService()
        {
            return new StartupAppsService(
                HkcuRoot,
                Hklm64Root,
                Hklm32Root,
                UserStartupFolder,
                CommonStartupFolder,
                ownsRoots: false);
        }

        public void SetHkcuApproval(string subKey, string valueName, byte[] value)
        {
            using var k = HkcuRoot.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{subKey}", writable: true)!;
            k.SetValue(valueName, value, RegistryValueKind.Binary);
        }

        public byte[]? GetHkcuApproval(string subKey, string valueName)
        {
            using var k = HkcuRoot.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{subKey}", writable: false);
            return k?.GetValue(valueName) as byte[];
        }

        public void SetHklmApproval(string subKey, string valueName, byte[] value)
        {
            using var k = Hklm64Root.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{subKey}", writable: true)!;
            k.SetValue(valueName, value, RegistryValueKind.Binary);
        }

        public byte[]? GetHklmApproval(string subKey, string valueName)
        {
            using var k = Hklm64Root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{subKey}", writable: false);
            return k?.GetValue(valueName) as byte[];
        }

        public void Dispose()
        {
            try { HkcuRun.Dispose(); } catch { }
            try { Hklm64Run.Dispose(); } catch { }
            try { Hklm32Run.Dispose(); } catch { }

            try { HkcuRoot.Dispose(); } catch { }
            try { Hklm64Root.Dispose(); } catch { }
            try { Hklm32Root.Dispose(); } catch { }
            try { TestRoot.Dispose(); } catch { }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(_testKeyPath, throwOnMissingSubKey: false);
            }
            catch { }

            try
            {
                string tempBase = Path.GetDirectoryName(UserStartupFolder)!;
                if (Directory.Exists(tempBase))
                {
                    Directory.Delete(tempBase, recursive: true);
                }
            }
            catch { }
        }
    }
}
