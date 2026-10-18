using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;
using VoltManager.Models;
using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public sealed class MaintenanceServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _backupDir;
    private readonly string _exclusionsPath;
    private readonly FakeMaintenanceEnvironment _env;

    public MaintenanceServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"vm_maint_test_{Guid.NewGuid():N}");
        _backupDir = Path.Combine(_testDir, "Backups");
        _exclusionsPath = Path.Combine(_testDir, "exclusions.json");
        _env = new FakeMaintenanceEnvironment();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch { }
    }

    private MaintenanceService CreateService(
        Func<string, CancellationToken, Task<bool>>? confirm = null,
        Func<string, object[], string>? localizer = null)
    {
        return new MaintenanceService(
            _env,
            confirm ?? ((_, _) => Task.FromResult(true)),
            localizer ?? ((key, args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, key, args)),
            _backupDir,
            _exclusionsPath);
    }

    [Fact]
    public async Task GetApps_ReturnsDiscoveredApps_OrdersByName()
    {
        _env.UninstallRecords.Add(new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "AppB",
            DisplayName: "Beta Tool", DisplayVersion: "1.0", Publisher: "Acme",
            InstallLocation: @"C:\Tools\Beta", UninstallString: @"C:\Tools\Beta\unins000.exe",
            QuietUninstallString: null, EstimatedSizeKb: 1024, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

        _env.UninstallRecords.Add(new RawUninstallRecord(
            Hive: "CurrentUser", View: "Registry64", SubKeyName: "AppA",
            DisplayName: "Alpha App", DisplayVersion: "2.0", Publisher: "Acme",
            InstallLocation: @"C:\Tools\Alpha", UninstallString: @"C:\Tools\Alpha\uninstall.exe",
            QuietUninstallString: null, EstimatedSizeKb: 2048, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

        _env.ExistingFiles.Add(@"C:\Tools\Beta\unins000.exe");
        _env.ExistingFiles.Add(@"C:\Tools\Alpha\uninstall.exe");

        using var service = CreateService();
        var result = await service.GetAppsAsync(CancellationToken.None);

        Assert.Equal(2, result.Apps.Count);
        Assert.Equal("Alpha App", result.Apps[0].Name);
        Assert.Equal("Beta Tool", result.Apps[1].Name);
        Assert.True(result.Apps[0].CanUninstall);
        Assert.True(result.Apps[1].CanUninstall);
    }

    [Fact]
    public async Task GetApps_ProtectsVoltManager_SystemComponents_WindowsUpdates()
    {
        _env.UninstallRecords.Add(new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "VoltManager",
            DisplayName: "VoltManager", DisplayVersion: "1.0", Publisher: "Miliano",
            InstallLocation: @"C:\Program Files\VoltManager", UninstallString: @"C:\Program Files\VoltManager\uninstall.exe",
            QuietUninstallString: null, EstimatedSizeKb: 5000, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

        _env.UninstallRecords.Add(new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "SysDriver",
            DisplayName: "Windows Driver Package", DisplayVersion: "1.0", Publisher: "Microsoft",
            InstallLocation: null, UninstallString: @"C:\Windows\System32\unins.exe",
            QuietUninstallString: null, EstimatedSizeKb: null, SystemComponent: 1,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

        _env.UninstallRecords.Add(new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "UpdateKB123456",
            DisplayName: "Security Update for Windows (KB123456)", DisplayVersion: null, Publisher: "Microsoft",
            InstallLocation: null, UninstallString: @"C:\Windows\System32\wusa.exe",
            QuietUninstallString: null, EstimatedSizeKb: null, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

        using var service = CreateService();
        var result = await service.GetAppsAsync(CancellationToken.None);

        Assert.Equal(3, result.Apps.Count);
        Assert.All(result.Apps, app => Assert.False(app.CanUninstall));
        Assert.Contains("VoltManager non può essere rimosso", result.Apps.Single(a => a.Name == "VoltManager").Reason);
        Assert.Contains("Componente di sistema", result.Apps.Single(a => a.Name == "Windows Driver Package").Reason);
        Assert.Contains("Aggiornamento o pacchetto", result.Apps.Single(a => a.Name.Contains("KB123456")).Reason);
    }

    [Fact]
    public async Task ValidateUninstallCommand_BlocksProhibitedInterpreters()
    {
        string[] prohibited = ["cmd.exe", "powershell.exe", "pwsh.exe", "cscript.exe", "wscript.exe", "bash.exe", "sh.exe", "rundll32.exe", "mshta.exe"];

        foreach (string interp in prohibited)
        {
            var env = new FakeMaintenanceEnvironment();
            env.UninstallRecords.Add(new RawUninstallRecord(
                Hive: "LocalMachine", View: "Registry64", SubKeyName: $"TestApp_{interp}",
                DisplayName: $"Dangerous App {interp}", DisplayVersion: "1.0", Publisher: "Malicious",
                InstallLocation: @"C:\App", UninstallString: $@"C:\Windows\System32\{interp} /c del C:\*",
                QuietUninstallString: null, EstimatedSizeKb: 100, SystemComponent: 0,
                ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

            env.ExistingFiles.Add($@"C:\Windows\System32\{interp}");

            using var service = new MaintenanceService(
                env,
                (_, _) => Task.FromResult(true),
                (k, a) => k,
                _backupDir,
                _exclusionsPath);

            var res = await service.GetAppsAsync(CancellationToken.None);
            var app = res.Apps.Single();
            Assert.False(app.CanUninstall);
            Assert.Contains("non consentito", app.Reason);
        }
    }

    [Theory]
    [InlineData(@"""C:\Tools\remove.cmd"" /silent")]
    [InlineData(@"""C:\Tools\remove.bat"" /silent")]
    [InlineData(@"""C:\Tools\remove.lnk"" /silent")]
    public async Task ValidateUninstallCommand_RejectsShellAssociatedTargets(string command)
    {
        string executable = command.Split('"')[1];
        _env.ExistingFiles.Add(executable);
        _env.UninstallRecords.Add(new RawUninstallRecord(
            "LocalMachine", "Registry64", "ShellAssociated", "Shell Associated", "1.0",
            "Vendor", @"C:\Tools", command, null, 100, 0, null, 0, null));
        using var service = CreateService();
        var app = Assert.Single((await service.GetAppsAsync(CancellationToken.None)).Apps);
        Assert.False(app.CanUninstall);
    }

    [Theory]
    [InlineData(@"cmd.exe /c msiexec.exe /x {12345678-1234-1234-1234-1234567890AB}",
        "{12345678-1234-1234-1234-1234567890AB}")]
    [InlineData(@"MsiExec.exe /x {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
        "{12345678-1234-1234-1234-1234567890AB}")]
    public async Task ValidateUninstallCommand_RejectsMsiCommandOrProductCodeSpoofing(
        string command, string subKeyName)
    {
        _env.UninstallRecords.Add(new RawUninstallRecord(
            "LocalMachine", "Registry64", subKeyName, "Unexpected MSI", "1.0",
            "Vendor", @"C:\Tools", command, null, 100, 0, null, 1, null));
        using var service = CreateService();
        var app = Assert.Single((await service.GetAppsAsync(CancellationToken.None)).Apps);
        Assert.False(app.CanUninstall);
    }

    [Fact]
    public async Task ValidateUninstallCommand_AllowsCoherentMsiProductCode()
    {
        const string productCode = "{12345678-1234-1234-1234-1234567890AB}";
        _env.UninstallRecords.Add(new RawUninstallRecord(
            "LocalMachine", "Registry64", productCode, "Legitimate MSI", "1.0",
            "Vendor", @"C:\Program Files\Vendor\App",
            $"MsiExec.exe /I{productCode}", null, 100, 0, null, 1, null));
        using var service = CreateService();
        var app = Assert.Single((await service.GetAppsAsync(CancellationToken.None)).Apps);
        Assert.True(app.CanUninstall);
    }

    [Fact]
    public async Task StartUninstall_WhenDeclined_ReleasesLockImmediately()
    {
        string appId = "uninstall:LocalMachine:Registry64:App1";
        _env.UninstallRecords.Add(new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "App1",
            DisplayName: "Test App", DisplayVersion: "1.0", Publisher: "Vendor",
            InstallLocation: @"C:\App1", UninstallString: @"C:\App1\unins.exe",
            QuietUninstallString: null, EstimatedSizeKb: 100, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null));

        _env.ExistingFiles.Add(@"C:\App1\unins.exe");

        // User declines confirmation
        using var service = CreateService(confirm: (_, _) => Task.FromResult(false));

        var op = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        Assert.Equal("cancelled", op.Status);

        // Verification: lock was released, so subsequent operation can be started without exception
        var op2 = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        Assert.Equal("cancelled", op2.Status);
    }

    [Fact]
    public async Task StartUninstall_WhenConfirmed_RunsProcess_And_VerifiesCompletion()
    {
        string appId = "uninstall:LocalMachine:Registry64:App1";
        var record = new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "App1",
            DisplayName: "Good App", DisplayVersion: "1.0", Publisher: "Vendor",
            InstallLocation: @"C:\GoodApp", UninstallString: @"C:\GoodApp\unins.exe",
            QuietUninstallString: null, EstimatedSizeKb: 100, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null);

        _env.UninstallRecords.Add(record);
        _env.ExistingFiles.Add(@"C:\GoodApp\unins.exe");

        _env.UninstallerHook = () =>
        {
            // Simulate uninstaller removing the key
            _env.UninstallRecords.Remove(record);
            return new ProcessExecutionResult(0, true, null);
        };

        using var service = CreateService(confirm: (_, _) => Task.FromResult(true));
        var op = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            op = service.GetOperationStatus(op.Id);
            if (op.Status == "completed")
                break;
        }

        Assert.Equal("completed", op.Status);
        Assert.True(op.CanScanLeftovers);
    }

    [Fact]
    public async Task CancelUninstall_DoesNotReleaseMutationLockWhileProcessIsRunning()
    {
        const string appId = "uninstall:LocalMachine:Registry64:SlowApp";
        var record = new RawUninstallRecord("LocalMachine", "Registry64", "SlowApp",
            "Slow App", "1.0", "Vendor", @"C:\SlowApp", @"C:\SlowApp\unins.exe",
            null, 100, 0, null, 0, null);
        _env.UninstallRecords.Add(record);
        _env.ExistingFiles.Add(@"C:\SlowApp\unins.exe");
        var processExit = new TaskCompletionSource<ProcessExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _env.AsyncUninstallerHook = () => processExit.Task;

        using var service = CreateService();
        var running = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        try
        {
            Assert.Equal("running", service.CancelOperation(running.Id).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartOperationAsync("uninstall", appId, CancellationToken.None));
        }
        finally
        {
            processExit.TrySetResult(new ProcessExecutionResult(1, true, null));
        }

        for (int i = 0; i < 100 && service.GetOperationStatus(running.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.Equal("failed", service.GetOperationStatus(running.Id).Status);
        var next = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        Assert.Equal("running", next.Status);
        for (int i = 0; i < 100 && service.GetOperationStatus(next.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.Equal("failed", service.GetOperationStatus(next.Id).Status);
    }

    [Fact]
    public async Task ManualVerification_PreservesOriginalSnapshotForLeftovers()
    {
        const string appId = "uninstall:LocalMachine:Registry64:RebootApp";
        const string location = @"C:\Vendor\RebootApp";
        var record = new RawUninstallRecord("LocalMachine", "Registry64", "RebootApp",
            "Reboot App", "1.0", "Vendor", location, @"C:\Vendor\RebootApp\unins.exe",
            null, 100, 0, null, 0, null);
        _env.UninstallRecords.Add(record);
        _env.ExistingFiles.Add(record.UninstallString!);
        _env.UninstallerHook = () =>
        {
            _env.UninstallRecords.Remove(record);
            return new ProcessExecutionResult(3010, true, null);
        };
        using var service = CreateService();
        var op = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        for (int i = 0; i < 100 && service.GetOperationStatus(op.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.Equal("rebootRequired", service.GetOperationStatus(op.Id).Status);

        var verified = await service.VerifyUninstallAsync(op.Id, CancellationToken.None);
        Assert.Equal("completed", verified.Status);
        _env.Directories.Add(location);
        _env.LeftoverFiles.Add(Path.Combine(location, "remainder.dll"));
        var leftovers = await service.StartOperationAsync("leftovers", appId, CancellationToken.None);
        for (int i = 0; i < 100 && service.GetOperationStatus(leftovers.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.Single(service.GetOperationStatus(leftovers.Id).Items);
    }

    [Fact]
    public async Task PartialUninstallInventory_NeverConfirmsRemoval()
    {
        const string appId = "uninstall:LocalMachine:Registry64:InventoryApp";
        var record = new RawUninstallRecord("LocalMachine", "Registry64", "InventoryApp",
            "Inventory App", "1.0", "Vendor", @"C:\InventoryApp", @"C:\InventoryApp\unins.exe",
            null, 100, 0, null, 0, null);
        _env.UninstallRecords.Add(record);
        _env.ExistingFiles.Add(record.UninstallString!);
        _env.UninstallerHook = () =>
        {
            _env.UninstallRecords.Remove(record);
            _env.FailUninstallInventory = true;
            return new ProcessExecutionResult(0, true, null);
        };
        using var service = CreateService();
        var op = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        for (int i = 0; i < 100 && service.GetOperationStatus(op.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.False(service.GetOperationStatus(op.Id).CanScanLeftovers);
        Assert.NotEqual("completed", service.GetOperationStatus(op.Id).Status);
        Assert.Equal("needsVerification",
            (await service.VerifyUninstallAsync(op.Id, CancellationToken.None)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartOperationAsync("leftovers", appId, CancellationToken.None));
    }

    [Fact]
    public async Task VerifyUninstall_CannotTreatRegistrationWithoutDisplayNameAsRemoved()
    {
        const string appId = "uninstall:LocalMachine:Registry64:HiddenKeyApp";
        var visible = new RawUninstallRecord("LocalMachine", "Registry64", "HiddenKeyApp",
            "Visible App", "1.0", "Vendor", @"C:\HiddenKeyApp", @"C:\HiddenKeyApp\unins.exe",
            null, 100, 0, null, 0, null);
        _env.UninstallRecords.Add(visible);
        _env.ExistingFiles.Add(visible.UninstallString!);
        _env.UninstallerHook = () =>
        {
            _env.UninstallRecords[0] = visible with { DisplayName = "" };
            return new ProcessExecutionResult(0, true, null);
        };
        using var service = CreateService();
        var op = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        for (int i = 0; i < 100 && service.GetOperationStatus(op.Id).Status == "running"; i++)
            await Task.Delay(20);

        var result = service.GetOperationStatus(op.Id);
        Assert.Equal("needsVerification", result.Status);
        Assert.False(result.CanScanLeftovers);
        Assert.Equal("needsVerification",
            (await service.VerifyUninstallAsync(op.Id, CancellationToken.None)).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartOperationAsync("leftovers", appId, CancellationToken.None));
    }

    [Fact]
    public async Task StartLeftoversScan_Throws_WhenAppNotVerifiedUninstalled()
    {
        using var service = CreateService();

        // Calling leftovers with arbitrary appId must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartOperationAsync("leftovers", "uninstall:LocalMachine:Registry64:ArbitraryApp", CancellationToken.None));

        // Calling leftovers with null appId must throw
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.StartOperationAsync("leftovers", null, CancellationToken.None));
    }

    [Fact]
    public async Task StartLeftoversScan_WhenAppVerifiedUninstalled_DiscoversFiles()
    {
        string appId = "uninstall:LocalMachine:Registry64:VerifiedApp";
        string installDir = @"C:\Tools\MyTool";
        var record = new RawUninstallRecord(
            Hive: "LocalMachine", View: "Registry64", SubKeyName: "VerifiedApp",
            DisplayName: "My Tool", DisplayVersion: "1.0", Publisher: "ToolsCo",
            InstallLocation: installDir, UninstallString: @"C:\Tools\MyTool\unins.exe",
            QuietUninstallString: null, EstimatedSizeKb: 100, SystemComponent: 0,
            ParentKeyName: null, WindowsInstaller: 0, PackageFullName: null);

        _env.UninstallRecords.Add(record);
        _env.ExistingFiles.Add(@"C:\Tools\MyTool\unins.exe");

        _env.UninstallerHook = () =>
        {
            _env.UninstallRecords.Remove(record);
            return new ProcessExecutionResult(0, true, null);
        };

        using var service = CreateService(confirm: (_, _) => Task.FromResult(true));

        var uninsOp = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            uninsOp = service.GetOperationStatus(uninsOp.Id);
            if (uninsOp.Status == "completed")
                break;
        }

        Assert.Equal("completed", uninsOp.Status);

        // Add leftover files in installLocation
        _env.Directories.Add(installDir);
        _env.LeftoverFiles.Add(Path.Combine(installDir, "leftover.dll"));
        _env.LeftoverFiles.Add(Path.Combine(installDir, "config.json"));
        _env.UserDataFiles.Add(Path.Combine(installDir, "config.json"));

        var leftoversOp = await service.StartOperationAsync("leftovers", appId, CancellationToken.None);
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            leftoversOp = service.GetOperationStatus(leftoversOp.Id);
            if (leftoversOp.Status == "completed")
                break;
        }

        Assert.Equal("completed", leftoversOp.Status);
        Assert.Equal(2, leftoversOp.Items.Count);

        var dllItem = leftoversOp.Items.Single(i => i.Path.EndsWith(".dll"));
        Assert.True(dllItem.Selectable);
        Assert.False(dllItem.ContainsUserData);

        var configItem = leftoversOp.Items.Single(i => i.Path.EndsWith(".json"));
        Assert.False(configItem.Selectable);
        Assert.True(configItem.ContainsUserData);
    }

    [Fact]
    public async Task LeftoversScan_SkipsInstallRootSharedWithAnotherRegisteredApplication()
    {
        const string appId = "uninstall:LocalMachine:Registry64:FirstApp";
        const string shared = @"C:\Tools\SharedSuite";
        var original = new RawUninstallRecord("LocalMachine", "Registry64", "FirstApp",
            "First App", "1.0", "Vendor", shared, @"C:\Tools\SharedSuite\uninstall.exe",
            null, 100, 0, null, 0, null);
        var other = original with
        {
            SubKeyName = "SecondApp",
            DisplayName = "Second App",
            InstallLocation = shared + @"\SecondApp"
        };
        _env.UninstallRecords.AddRange([original, other]);
        _env.ExistingFiles.Add(original.UninstallString!);
        _env.UninstallerHook = () =>
        {
            _env.UninstallRecords.Remove(original);
            return new ProcessExecutionResult(0, true, null);
        };
        using var service = CreateService();
        var uninstall = await service.StartOperationAsync("uninstall", appId, CancellationToken.None);
        for (int i = 0; i < 100 && service.GetOperationStatus(uninstall.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.Equal("completed", service.GetOperationStatus(uninstall.Id).Status);

        _env.Directories.Add(shared);
        _env.LeftoverFiles.Add(Path.Combine(shared, "shared.dll"));
        var scan = await service.StartOperationAsync("leftovers", appId, CancellationToken.None);
        for (int i = 0; i < 100 && service.GetOperationStatus(scan.Id).Status == "running"; i++)
            await Task.Delay(20);
        Assert.Equal("completed", service.GetOperationStatus(scan.Id).Status);
        Assert.Empty(service.GetOperationStatus(scan.Id).Items);
    }

    [Fact]
    public async Task RegistryScan_FindsMissingRunAndAppPaths_MarksSelectableTrue()
    {
        _env.RunValues.Add(new RawRegistryValueRecord(
            Hive: "CurrentUser", View: "Registry64", SubKey: @"Software\Microsoft\Windows\CurrentVersion\Run",
            ValueName: "DeadApp", ValueKind: "String", ValueData: @"C:\Gone\app.exe",
            ExtractedExecutablePath: @"C:\Gone\app.exe"));

        _env.AppPaths.Add(new RawAppPathRecord(
            Hive: "LocalMachine", View: "Registry64", SubKey: @"Software\Microsoft\Windows\CurrentVersion\App Paths\missing.exe",
            DefaultValue: @"C:\Gone\missing.exe", PathValue: null));

        _env.TrulyMissingFiles.Add(@"C:\Gone\app.exe");
        _env.TrulyMissingFiles.Add(@"C:\Gone\missing.exe");
        _env.RegistryStorage[@"LocalMachine:Registry64:Software\Microsoft\Windows\CurrentVersion\App Paths\missing.exe:"] =
            new RawRegistryValueRecord("LocalMachine", "Registry64", @"Software\Microsoft\Windows\CurrentVersion\App Paths\missing.exe", "", "String", @"C:\Gone\missing.exe", @"C:\Gone\missing.exe");

        using var service = CreateService();
        var op = await service.StartOperationAsync("registry", null, CancellationToken.None);

        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(50);
            op = service.GetOperationStatus(op.Id);
            if (op.Status == "completed")
                break;
        }

        Assert.Equal("completed", op.Status);
        Assert.Equal(2, op.Items.Count);
        // CRITICAL: Items must be selectable by default for cleaning
        Assert.All(op.Items, item => Assert.True(item.Selectable));
        Assert.All(op.Items, item => Assert.Equal("registry", item.Kind));
    }

    [Fact]
    public async Task ApplyOperation_CreatesBackup_And_DeletesItems()
    {
        string dummyFile = Path.Combine(_testDir, "file_to_delete.txt");
        _env.CreateDirectory(_testDir);
        _env.WriteAllText(dummyFile, "Some content");

        // Set up registry value
        _env.RegistryStorage["CurrentUser:Registry64:Software\\Test:DeadVal"] = new RawRegistryValueRecord(
            "CurrentUser", "Registry64", "Software\\Test", "DeadVal", "String", @"C:\Gone\app.exe", @"C:\Gone\app.exe");
        _env.TrulyMissingFiles.Add(@"C:\Gone\app.exe");

        var items = new List<MaintenanceOperationItem>
        {
            new("regval|CurrentUser|Registry64|Software\\Test|DeadVal", "registry", "[CurrentUser:Registry64] Software\\Test -> DeadVal", "Dead", Selectable: true, ContainsUserData: false)
                { RegistryValue = _env.RegistryStorage["CurrentUser:Registry64:Software\\Test:DeadVal"] },
            new($"leftover|file|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dummyFile.ToLowerInvariant())))[..16]}", "file", dummyFile, "Leftover", Selectable: true, ContainsUserData: false)
                { InstallRoot = _testDir }
        };

        var scanOp = new MaintenanceOperation(
            Id: "op-apply-test", Kind: "registry", Status: "completed", Message: "Done",
            Items: items, Processed: 0, Total: 2);

        using var service = CreateService(confirm: (_, _) => Task.FromResult(true));

        typeof(MaintenanceService)
            .GetMethod("RegisterOperation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [scanOp.Id, scanOp, new CancellationTokenSource()]);

        var applied = await service.ApplyOperationAsync(scanOp.Id, items.Select(i => i.Id).ToList(), CancellationToken.None);

        Assert.Equal("completed", applied.Status);
        Assert.Equal(2, applied.Processed);
        Assert.NotNull(applied.BackupId);

        // Verification: file was deleted
        Assert.False(_env.FileExists(dummyFile));

        // Verification: registry value was deleted
        Assert.Null(_env.ReadRegistryValue("CurrentUser", "Registry64", "Software\\Test", "DeadVal"));

        // Verification: backup was created
        var backup = service.GetBackup(applied.BackupId);
        Assert.Equal("active", backup.Status);
        Assert.Equal(2, backup.Items.Count);
    }

    [Fact]
    public async Task RestoreBackup_RestoresRegistryAndFiles()
    {
        string dummyFile = Path.Combine(_testDir, "restore_test.txt");
        _env.CreateDirectory(_testDir);
        _env.WriteAllText(dummyFile, "Hello World");

        _env.RegistryStorage["CurrentUser:Registry64:Software\\Test:RestoreVal"] = new RawRegistryValueRecord(
            "CurrentUser", "Registry64", "Software\\Test", "RestoreVal", "String", "SavedVal", @"C:\Gone\app.exe");
        _env.TrulyMissingFiles.Add(@"C:\Gone\app.exe");

        var items = new List<MaintenanceOperationItem>
        {
            new("regval|CurrentUser|Registry64|Software\\Test|RestoreVal", "registry", "[CurrentUser:Registry64] Software\\Test -> RestoreVal", "Test", Selectable: true, ContainsUserData: false)
                { RegistryValue = _env.RegistryStorage["CurrentUser:Registry64:Software\\Test:RestoreVal"] },
            new($"leftover|file|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dummyFile.ToLowerInvariant())))[..16]}", "file", dummyFile, "Test", Selectable: true, ContainsUserData: false)
                { InstallRoot = _testDir }
        };

        var scanOp = new MaintenanceOperation(
            Id: "op-restore-test", Kind: "registry", Status: "completed", Message: "Done",
            Items: items, Processed: 0, Total: 2);

        using var service = CreateService(confirm: (_, _) => Task.FromResult(true));

        typeof(MaintenanceService)
            .GetMethod("RegisterOperation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [scanOp.Id, scanOp, new CancellationTokenSource()]);

        var applied = await service.ApplyOperationAsync(scanOp.Id, items.Select(i => i.Id).ToList(), CancellationToken.None);
        string backupId = applied.BackupId!;

        // Now items are deleted
        Assert.False(_env.FileExists(dummyFile));
        Assert.Null(_env.ReadRegistryValue("CurrentUser", "Registry64", "Software\\Test", "RestoreVal"));

        // Restore them
        var restoreOp = await service.RestoreBackupAsync(backupId, items.Select(i => i.Id).ToList(), CancellationToken.None);
        Assert.Equal("completed", restoreOp.Status);
        Assert.Equal(2, restoreOp.Processed);

        // Verify restoration
        Assert.True(_env.FileExists(dummyFile));
        Assert.Equal("Hello World", _env.ReadAllText(dummyFile));
        Assert.NotNull(_env.ReadRegistryValue("CurrentUser", "Registry64", "Software\\Test", "RestoreVal"));
    }

    [Fact]
    public async Task ApplyOperation_RejectsMixedKnownAndUnknownItemIds_WithoutDeletingAnything()
    {
        const string key = @"CurrentUser:Registry64:Software\Test:Dead";
        var value = new RawRegistryValueRecord("CurrentUser", "Registry64", @"Software\Test", "Dead",
            "String", @"C:\Gone\tool.exe", @"C:\Gone\tool.exe");
        _env.RegistryStorage[key] = value;
        _env.TrulyMissingFiles.Add(@"C:\Gone\tool.exe");
        var item = new MaintenanceOperationItem(@"regval|CurrentUser|Registry64|Software\Test|Dead",
            "registry", @"[CurrentUser:Registry64] Software\Test -> Dead", "Missing executable", true, false)
            { RegistryValue = value };
        var scan = new MaintenanceOperation("test-mixed-ids", "registry", "completed", "done", [item], 0, 1);
        using var service = CreateService();
        typeof(MaintenanceService).GetMethod("RegisterOperation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [scan.Id, scan, new CancellationTokenSource()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyOperationAsync(scan.Id, [item.Id, "unknown-selected-id"], CancellationToken.None));
        Assert.NotNull(_env.ReadRegistryValue("CurrentUser", "Registry64", @"Software\Test", "Dead"));
    }

    [Fact]
    public async Task ApplyOperation_RejectsRegistryItemWhoseIdDoesNotMatchItsMetadata()
    {
        const string key = @"CurrentUser:Registry64:Software\Test:Actual";
        var value = new RawRegistryValueRecord("CurrentUser", "Registry64", @"Software\Test", "Actual",
            "String", @"C:\Gone\tool.exe", @"C:\Gone\tool.exe");
        _env.RegistryStorage[key] = value;
        _env.TrulyMissingFiles.Add(@"C:\Gone\tool.exe");
        var item = new MaintenanceOperationItem(@"regval|CurrentUser|Registry64|Software\Test:Other|Fake",
            "registry", @"[CurrentUser:Registry64] Software\Test -> Actual", "Missing executable", true, false)
            { RegistryValue = value };
        var scan = new MaintenanceOperation("test-mismatched-identity", "registry", "completed", "done", [item], 0, 1);
        using var service = CreateService();
        typeof(MaintenanceService).GetMethod("RegisterOperation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [scan.Id, scan, new CancellationTokenSource()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyOperationAsync(scan.Id, [item.Id], CancellationToken.None));
        Assert.NotNull(_env.ReadRegistryValue("CurrentUser", "Registry64", @"Software\Test", "Actual"));
    }

    [Fact]
    public async Task ApplyOperation_DoesNotDeleteRegistryValueReplacedJustBeforeMutation()
    {
        const string key = @"CurrentUser:Registry64:Software\Test:Contended";
        var oldValue = new RawRegistryValueRecord("CurrentUser", "Registry64", @"Software\Test",
            "Contended", "String", @"C:\Gone\obsolete.exe", @"C:\Gone\obsolete.exe");
        var newValue = oldValue with
        {
            ValueData = @"C:\Live\replacement.exe",
            ExtractedExecutablePath = @"C:\Live\replacement.exe"
        };
        _env.RegistryStorage[key] = oldValue;
        _env.TrulyMissingFiles.Add(@"C:\Gone\obsolete.exe");
        _env.BeforeDeleteRegistryValue = () => _env.RegistryStorage[key] = newValue;
        var item = new MaintenanceOperationItem(@"regval|CurrentUser|Registry64|Software\Test|Contended",
            "registry", @"[CurrentUser:Registry64] Software\Test -> Contended", "Missing executable", true, false)
            { RegistryValue = oldValue };
        var scan = new MaintenanceOperation("test-concurrent-registry", "registry", "completed",
            "done", [item], 0, 1);
        using var service = CreateService();
        typeof(MaintenanceService).GetMethod("RegisterOperation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(service, [scan.Id, scan, new CancellationTokenSource()]);

        var result = await service.ApplyOperationAsync(scan.Id, [item.Id], CancellationToken.None);
        Assert.Equal(0, result.Processed);
        Assert.Equal(@"C:\Live\replacement.exe",
            _env.ReadRegistryValue("CurrentUser", "Registry64", @"Software\Test", "Contended")?.ValueData);
    }

    [Fact]
    public void ExtractStringArray_EnforcesFailClosedAndLimits()
    {
        // 1. Valid array
        var validPayload = Payload(new { itemIds = new[] { "id1", "id2", "id3" } });
        var list = MaintenanceRpcHandler.ExtractStringArray(validPayload, "itemIds");
        Assert.Equal(["id1", "id2", "id3"], list);

        // 2. Deduplicates repeated entries
        var dupPayload = Payload(new { itemIds = new[] { "id1", "id2", "id1", "id3" } });
        var dupList = MaintenanceRpcHandler.ExtractStringArray(dupPayload, "itemIds");
        Assert.Equal(["id1", "id2", "id3"], dupList);

        // 3. Rejects non-string element (fail-closed)
        var invalidTypePayload = Payload(new { itemIds = new object[] { "id1", 123 } });
        Assert.Throws<ArgumentException>(() =>
            MaintenanceRpcHandler.ExtractStringArray(invalidTypePayload, "itemIds"));

        // 4. Rejects empty string (fail-closed)
        var emptyStringPayload = Payload(new { itemIds = new[] { "id1", "", "id2" } });
        Assert.Throws<ArgumentException>(() =>
            MaintenanceRpcHandler.ExtractStringArray(emptyStringPayload, "itemIds"));

        // 5. Rejects whitespace string (fail-closed)
        var whitespacePayload = Payload(new { itemIds = new[] { "id1", "   " } });
        Assert.Throws<ArgumentException>(() =>
            MaintenanceRpcHandler.ExtractStringArray(whitespacePayload, "itemIds"));

        // 6. Rejects array exceeding limit of 1000 items (boundary test)
        var largeList = Enumerable.Range(0, 1001).Select(i => $"id_{i}").ToArray();
        var largePayload = Payload(new { itemIds = largeList });
        Assert.Throws<ArgumentException>(() =>
            MaintenanceRpcHandler.ExtractStringArray(largePayload, "itemIds"));

        // 7. Exactly 1000 items succeeds
        var maxList = Enumerable.Range(0, 1000).Select(i => $"id_{i}").ToArray();
        var maxPayload = Payload(new { itemIds = maxList });
        var maxResult = MaintenanceRpcHandler.ExtractStringArray(maxPayload, "itemIds");
        Assert.Equal(1000, maxResult.Count);
    }

    [Fact]
    public void IsProtectedSystemPath_ProtectsSystemSubtreesAndRoots()
    {
        var env = new WindowsMaintenanceEnvironment();

        // 1. Drive roots are protected
        Assert.True(env.IsProtectedSystemPath(@"C:\"));
        Assert.True(env.IsProtectedSystemPath(@"C:"));

        // 2. Windows and System32 subtrees are protected
        string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.True(env.IsProtectedSystemPath(winDir));
        Assert.True(env.IsProtectedSystemPath(Path.Combine(winDir, "System32")));
        Assert.True(env.IsProtectedSystemPath(Path.Combine(winDir, "System32", "notepad.exe")));

        // 3. Desktop and Documents subtrees are protected
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs))
        {
            Assert.True(env.IsProtectedSystemPath(docs));
            Assert.True(env.IsProtectedSystemPath(Path.Combine(docs, "Secret.docx")));
        }

        // 4. Program Files direct root is protected
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(env.IsProtectedSystemPath(pf));

        // 5. Shared vendor directories under Program Files are protected
        Assert.True(env.IsProtectedSystemPath(Path.Combine(pf, "Common Files")));
        Assert.True(env.IsProtectedSystemPath(Path.Combine(pf, "Microsoft")));

        // 6. Dedicated application folder under Program Files is NOT protected (safe for leftovers)
        Assert.False(env.IsProtectedSystemPath(Path.Combine(pf, "VendorApp_Test_XYZ")));
        Assert.False(env.IsProtectedSystemPath(Path.Combine(pf, "VendorApp_Test_XYZ", "app.dll")));
    }

    [Fact]
    public async Task RpcHandler_RoutesAll12Methods()
    {
        using var service = CreateService();
        var handler = new MaintenanceRpcHandler(service);

        Assert.Equal(12, MaintenanceRpcHandler.MethodNames.Count);

        // 1. getApps
        var appsObj = await handler.HandleAsync("maintenance.getApps", Payload(new { }), CancellationToken.None);
        Assert.NotNull(appsObj);

        // 2. start
        var startObj = await handler.HandleAsync("maintenance.start", Payload(new { kind = "registry" }), CancellationToken.None);
        var op = Assert.IsType<MaintenanceOperation>(startObj);

        // 3. status
        var statusObj = await handler.HandleAsync("maintenance.status", Payload(new { operationId = op.Id }), CancellationToken.None);
        Assert.IsType<MaintenanceOperation>(statusObj);

        // 4. cancel
        var cancelObj = await handler.HandleAsync("maintenance.cancel", Payload(new { operationId = op.Id }), CancellationToken.None);
        Assert.IsType<MaintenanceOperation>(cancelObj);

        // 5. verifyUninstall
        var verifyObj = await handler.HandleAsync("maintenance.verifyUninstall", Payload(new { operationId = op.Id }), CancellationToken.None);
        Assert.IsType<MaintenanceOperation>(verifyObj);

        // 6. getBackups
        var backupsObj = await handler.HandleAsync("maintenance.getBackups", Payload(new { }), CancellationToken.None);
        Assert.NotNull(backupsObj);

        // 7. openAppsSettings
        var settingsObj = await handler.HandleAsync("maintenance.openAppsSettings", Payload(new { }), CancellationToken.None);
        Assert.NotNull(settingsObj);

        // 8. exclude
        var excludeObj = await handler.HandleAsync("maintenance.exclude", Payload(new { operationId = op.Id, itemIds = new[] { "id1" } }), CancellationToken.None);
        Assert.NotNull(excludeObj);
    }

    [Fact]
    public void Backup_RejectsCorruptCopy_AndRestoreNeverOverwritesRecreatedFile()
    {
        string file = Path.Combine(_testDir, "payload.dll");
        _env.CreateDirectory(_testDir);
        _env.WriteFileBytes(file, [1, 2, 3]);
        var item = new MaintenanceOperationItem("file:id", "file", file, "fixture", true, false);
        var manager = new MaintenanceBackupManager(_backupDir, _env);
        _env.AfterCopy = destination => _env.WriteFileBytes(destination, [9]);
        Assert.Throws<InvalidDataException>(() => manager.CreateBackup("leftovers", "fixture", [item]));
        _env.AfterCopy = null;
        string backup = manager.CreateBackup("leftovers", "fixture", [item]);
        _env.WriteFileBytes(file, [7]);
        var restored = manager.RestoreBackup(backup, []);
        Assert.Equal(0, restored.Processed);
        Assert.Equal(new byte[] { 7 }, _env.ReadFileBytes(file));
    }

    [Fact]
    public void MissingTarget_RejectsUnquotedCommandWithSpaces()
    {
        var env = new WindowsMaintenanceEnvironment();
        string? executable = WindowsMaintenanceEnvironment.ExtractExecutablePath(@"C:\Program Files\MissingTool\app.exe --run");
        Assert.Null(executable);
        Assert.False(env.CheckFileIsTrulyMissing(@"\\server\share\missing.exe", out _, out _));
    }

    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    private sealed class FakeMaintenanceEnvironment : IMaintenanceEnvironment
    {
        public List<RawUninstallRecord> UninstallRecords { get; } = new();
        public List<RawRegistryValueRecord> RunValues { get; } = new();
        public List<RawAppPathRecord> AppPaths { get; } = new();
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TrulyMissingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> LeftoverFiles { get; } = new();
        public HashSet<string> UserDataFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> FileStorage { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RawRegistryValueRecord> RegistryStorage { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Action<string>? AfterCopy { get; set; }
        public Action? BeforeDeleteRegistryValue { get; set; }
        public Func<ProcessExecutionResult>? UninstallerHook { get; set; }
        public Func<Task<ProcessExecutionResult>>? AsyncUninstallerHook { get; set; }
        public bool FailUninstallInventory { get; set; }
        public (string Exe, string Args)? LastExecutedProcess { get; private set; }

        public IReadOnlyList<RawUninstallRecord> GetUninstallRecords()
            => FailUninstallInventory
                ? throw new IOException("Incomplete uninstall inventory")
                : UninstallRecords.ToList();

        public bool IsUninstallRegistrationPresent(string hive, string view, string subKeyName)
            => FailUninstallInventory || UninstallRecords.Any(record =>
                string.Equals(record.Hive, hive, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.View, view, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.SubKeyName, subKeyName, StringComparison.OrdinalIgnoreCase));
        public IReadOnlyList<RawRegistryValueRecord> GetStartupRunValues() => RunValues;
        public IReadOnlyList<RawAppPathRecord> GetAppPathsRecords() => AppPaths;

        public bool DeleteRegistryValue(string hive, string view, string subKey, string valueName)
        {
            BeforeDeleteRegistryValue?.Invoke();
            string key = $"{hive}:{view}:{subKey}:{valueName}";
            return RegistryStorage.Remove(key);
        }

        public bool DeleteRegistryValueIfUnchanged(RawRegistryValueRecord expected)
        {
            BeforeDeleteRegistryValue?.Invoke();
            var actual = ReadRegistryValue(expected.Hive, expected.View, expected.SubKey, expected.ValueName);
            if (actual == null || !MaintenanceBackupManager.RegistryEquals(actual, expected))
                return false;
            return RegistryStorage.Remove($"{expected.Hive}:{expected.View}:{expected.SubKey}:{expected.ValueName}");
        }

        public bool SetRegistryValue(string hive, string view, string subKey, string valueName, string valueKind, string valueDataBase64)
        {
            string key = $"{hive}:{view}:{subKey}:{valueName}";
            byte[] raw = Convert.FromBase64String(valueDataBase64);
            object data = WindowsMaintenanceEnvironment.DeserializeRegistryValue(raw, Enum.Parse<Microsoft.Win32.RegistryValueKind>(valueKind));
            RegistryStorage[key] = new RawRegistryValueRecord(hive, view, subKey, valueName, valueKind, data, null);
            return true;
        }

        public bool TrySetRegistryValueIfAbsent(string hive, string view, string subKey, string valueName, string valueKind, string valueDataBase64)
        {
            if (ReadRegistryValue(hive, view, subKey, valueName) != null)
                return false;
            return SetRegistryValue(hive, view, subKey, valueName, valueKind, valueDataBase64);
        }

        public RawRegistryValueRecord? ReadRegistryValue(string hive, string view, string subKey, string valueName)
        {
            string key = $"{hive}:{view}:{subKey}:{valueName}";
            return RegistryStorage.TryGetValue(key, out var val) ? val : null;
        }

        public bool CheckFileIsTrulyMissing(string rawPath, out string normalizedPath, out string reason)
        {
            normalizedPath = rawPath;
            reason = "Test missing";
            return TrulyMissingFiles.Contains(rawPath);
        }

        public IReadOnlyList<string> FindLeftoverFiles(string installLocation, int maxResults)
            => LeftoverFiles.Take(maxResults).ToList();

        public bool IsProtectedSystemPath(string path)
            => path.Contains("Windows", StringComparison.OrdinalIgnoreCase) || path.Contains("System32", StringComparison.OrdinalIgnoreCase);

        public bool IsSafeLocalPath(string path) => Path.IsPathRooted(path);

        public void SecureBackupDirectory(string path) { }

        public bool ContainsUserData(string path) => UserDataFiles.Contains(path);

        public Task<ProcessExecutionResult> RunUninstallerAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            LastExecutedProcess = (fileName, arguments);
            if (AsyncUninstallerHook != null)
                return AsyncUninstallerHook();
            if (UninstallerHook != null)
                return Task.FromResult(UninstallerHook());

            return Task.FromResult(new ProcessExecutionResult(0, Completed: true, null));
        }

        public bool OpenAppsSettings() => true;

        public string ReadAllText(string path)
        {
            if (FileStorage.TryGetValue(path, out var bytes))
                return Encoding.UTF8.GetString(bytes);
            return File.ReadAllText(path);
        }

        public void WriteAllText(string path, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            FileStorage[path] = bytes;
            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
                Directories.Add(dir);
            try { File.WriteAllText(path, text); } catch { }
        }

        public bool FileExists(string path)
        {
            return ExistingFiles.Contains(path) || FileStorage.ContainsKey(path) || File.Exists(path);
        }

        public bool DirectoryExists(string path)
        {
            return Directories.Contains(path) || Directory.Exists(path);
        }

        public void CreateDirectory(string path)
        {
            Directories.Add(path);
            try { Directory.CreateDirectory(path); } catch { }
        }

        public void DeleteDirectory(string path, bool recursive)
        {
            Directories.Remove(path);
            try { Directory.Delete(path, recursive); } catch { }
        }

        public void DeleteFile(string path)
        {
            ExistingFiles.Remove(path);
            FileStorage.Remove(path);
            try { File.Delete(path); } catch { }
        }

        public void CopyFile(string source, string destination, bool overwrite)
        {
            if (!overwrite && FileExists(destination)) throw new IOException("Destination already exists.");
            byte[] bytes = ReadFileBytes(source);
            WriteFileBytes(destination, bytes);
            AfterCopy?.Invoke(destination);
        }

        public byte[] ReadFileBytes(string path)
        {
            if (FileStorage.TryGetValue(path, out var bytes))
                return bytes;
            return File.ReadAllBytes(path);
        }

        public void WriteFileBytes(string path, byte[] bytes)
        {
            FileStorage[path] = bytes;
            string? dir = Path.GetDirectoryName(path);
            if (dir != null)
                Directories.Add(dir);
            try { File.WriteAllBytes(path, bytes); } catch { }
        }
    }
}
