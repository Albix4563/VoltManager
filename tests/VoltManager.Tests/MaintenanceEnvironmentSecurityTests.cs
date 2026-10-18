using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using System.Security;
using VoltManager.Services;
using Xunit;

namespace VoltManager.Tests;

public sealed class MaintenanceEnvironmentSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoltManagerMaintenanceSecurity", Guid.NewGuid().ToString("N"));
    private readonly WindowsMaintenanceEnvironment _environment = new();

    [Theory]
    [InlineData(@"C:\Program Files\Vendor\uninstall.exe /quiet")]
    [InlineData(@"C:\Program Files (x86)\Vendor\uninstall.exe")]
    [InlineData(@"C:\Tools\App Name\uninstall.exe /quiet")]
    [InlineData(@"C:\Program.exe Files\Vendor\uninstall.exe /quiet")]
    public void ExtractExecutablePath_RejectsAmbiguousUnquotedPaths(string command)
    {
        Assert.Null(WindowsMaintenanceEnvironment.ExtractExecutablePath(command));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Vendor\\uninstall.exe\" /quiet", @"C:\Program Files\Vendor\uninstall.exe")]
    [InlineData(@"C:\Tools\uninstall.exe /quiet", @"C:\Tools\uninstall.exe")]
    public void ExtractExecutablePath_PreservesUnambiguousCommands(string command, string expected)
    {
        Assert.Equal(expected, WindowsMaintenanceEnvironment.ExtractExecutablePath(command));
    }

    [Fact]
    public void FileExists_AccessDeniedMustNotBeReportedAsMissing()
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "restricted.exe");
        File.WriteAllText(file, "fixture");

        Assert.True(_environment.FileExists(file));
        Assert.False(_environment.CheckFileIsTrulyMissing(file, out _, out _));

        // NTFS grants read-attributes to owners on some Windows setups, so simulate the
        // exact error returned by File.GetAttributes rather than relying on ACL privileges.
        Assert.True(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => throw new UnauthorizedAccessException()));
        Assert.True(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => throw new SecurityException()));
        Assert.True(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => throw new IOException()));
        Assert.False(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => throw new FileNotFoundException()));
        Assert.False(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => throw new DirectoryNotFoundException()));
        Assert.False(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => FileAttributes.Directory));
        Assert.True(WindowsMaintenanceEnvironment.FileExistsFailClosed(file, _ => FileAttributes.Normal));
    }

    [Fact]
    public void TrySetRegistryValueIfAbsent_ProtectsExistingValueRegardlessOfNameCase()
    {
        string subKey = @"Software\VoltManager.Tests\MaintenanceEnvironmentSecurity\" + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey);
            Assert.NotNull(key);
            key.SetValue("Recovered", "current user value", RegistryValueKind.String);
            string previous = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("old backup"));

            Assert.False(_environment.TrySetRegistryValueIfAbsent(
                "CurrentUser", "Registry64", subKey, "RECOVERED", "String", previous));
            Assert.Equal("current user value", key.GetValue("Recovered"));

            key.DeleteValue("Recovered");
            Assert.True(_environment.TrySetRegistryValueIfAbsent(
                "CurrentUser", "Registry64", subKey, "Recovered", "String", previous));
            Assert.Equal("old backup", key.GetValue("Recovered"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void DeleteRegistryValueIfUnchanged_PreservesValueReplacedAfterSnapshot()
    {
        string subKey = RegistryTestSubKey();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(subKey);
            Assert.NotNull(key);
            key.SetValue("Startup", "original command", RegistryValueKind.String);

            RawRegistryValueRecord snapshot = SnapshotRegistryValue(subKey, "Startup");
            key.SetValue("Startup", "new command", RegistryValueKind.String);

            Assert.False(_environment.DeleteRegistryValueIfUnchanged(snapshot));
            Assert.Equal("new command", key.GetValue("Startup"));
        }
        finally
        {
            DeleteRegistryTestSubKey(subKey);
        }
    }

    [Fact]
    public void DeleteRegistryValueIfUnchanged_PreservesChangedKindEvenWhenTextMatches()
    {
        string subKey = RegistryTestSubKey();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(subKey);
            Assert.NotNull(key);
            key.SetValue("Startup", "42", RegistryValueKind.String);

            RawRegistryValueRecord snapshot = SnapshotRegistryValue(subKey, "Startup");
            key.SetValue("Startup", "42", RegistryValueKind.ExpandString);

            Assert.False(_environment.DeleteRegistryValueIfUnchanged(snapshot));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Startup"));
        }
        finally
        {
            DeleteRegistryTestSubKey(subKey);
        }
    }

    [Fact]
    public void DeleteRegistryValueIfUnchanged_PreservesModifiedBinaryData()
    {
        string subKey = RegistryTestSubKey();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(subKey);
            Assert.NotNull(key);
            key.SetValue("Startup", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);

            RawRegistryValueRecord snapshot = SnapshotRegistryValue(subKey, "Startup");
            key.SetValue("Startup", new byte[] { 1, 2, 4 }, RegistryValueKind.Binary);

            Assert.False(_environment.DeleteRegistryValueIfUnchanged(snapshot));
            Assert.Equal(new byte[] { 1, 2, 4 }, key.GetValue("Startup"));
        }
        finally
        {
            DeleteRegistryTestSubKey(subKey);
        }
    }

    [Fact]
    public void DeleteRegistryValueIfUnchanged_DeletesOnlyMatchingValue()
    {
        string subKey = RegistryTestSubKey();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(subKey);
            Assert.NotNull(key);
            key.SetValue("Startup", "original command", RegistryValueKind.String);
            key.SetValue("Other", "preserve", RegistryValueKind.String);

            RawRegistryValueRecord snapshot = SnapshotRegistryValue(subKey, "Startup");

            Assert.True(_environment.DeleteRegistryValueIfUnchanged(snapshot));
            Assert.DoesNotContain("Startup", key.GetValueNames());
            Assert.Equal("preserve", key.GetValue("Other"));
            Assert.False(_environment.DeleteRegistryValueIfUnchanged(snapshot));
        }
        finally
        {
            DeleteRegistryTestSubKey(subKey);
        }
    }

    private RawRegistryValueRecord SnapshotRegistryValue(string subKey, string valueName)
        => _environment.ReadRegistryValue("CurrentUser", "Registry64", subKey, valueName)
            ?? throw new InvalidOperationException("Unable to capture registry fixture.");

    private static string RegistryTestSubKey()
        => @"Software\VoltManager.Tests\MaintenanceEnvironmentSecurity\" + Guid.NewGuid().ToString("N");

    private static void DeleteRegistryTestSubKey(string subKey)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
    }

    [Fact]
    public void GetUninstallRecords_PreservesUnnamedRegistrationForOverlapSafety()
    {
        string name = "VoltManagerHidden_" + Guid.NewGuid().ToString("N");
        string uninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
        string installLocation = @"C:\Program Files\HiddenApplicationFixture";
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var key = baseKey.CreateSubKey(uninstallSubKey);
            Assert.NotNull(key);
            key.SetValue("DisplayName", "", RegistryValueKind.String);
            key.SetValue("InstallLocation", installLocation, RegistryValueKind.String);

            RawUninstallRecord record = Assert.Single(_environment.GetUninstallRecords(),
                record => record.Hive == "CurrentUser" &&
                          record.View == "Registry64" &&
                          record.SubKeyName == name);
            Assert.Equal("", record.DisplayName);
            Assert.Equal(installLocation, record.InstallLocation);
        }
        finally
        {
            baseKey.DeleteSubKeyTree(uninstallSubKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void IsUninstallRegistrationPresent_DetectsKeyEvenWithBlankDisplayName()
    {
        string name = "VoltManagerSecurity_" + Guid.NewGuid().ToString("N");
        string uninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name;
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        try
        {
            using var key = baseKey.CreateSubKey(uninstallSubKey);
            Assert.NotNull(key);
            key.SetValue("DisplayName", "", RegistryValueKind.String);

            Assert.True(_environment.IsUninstallRegistrationPresent("CurrentUser", "Registry64", name));
        }
        finally
        {
            baseKey.DeleteSubKeyTree(uninstallSubKey, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void IsUninstallRegistrationPresent_ReportsConfirmedAbsence()
    {
        string missing = "VoltManagerMissing_" + Guid.NewGuid().ToString("N");
        Assert.False(_environment.IsUninstallRegistrationPresent("CurrentUser", "Registry64", missing));
    }

    [Theory]
    [InlineData("InvalidHive", "Registry64", "UninstallEntry")]
    [InlineData("CurrentUser", "InvalidView", "UninstallEntry")]
    [InlineData("CurrentUser", "Registry64", "")]
    [InlineData("CurrentUser", "Registry64", @"Nested\Path")]
    public void IsUninstallRegistrationPresent_UnknownIdentityFailsClosed(
        string hive, string view, string name)
    {
        Assert.True(_environment.IsUninstallRegistrationPresent(hive, view, name));
    }

    [Fact]
    public void IsSafeLocalPath_RejectsReparsePointInAncestor()
    {
        Directory.CreateDirectory(_root);
        string actual = Path.Combine(_root, "actual");
        string link = Path.Combine(_root, "redirect");
        Directory.CreateDirectory(actual);
        File.WriteAllText(Path.Combine(actual, "found.exe"), "fixture");

        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, actual);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                using var cmd = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c mklink /J \"{link}\" \"{actual}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }) ?? throw new InvalidOperationException("Unable to create junction fixture.");
                cmd.WaitForExit();
                Assert.True(cmd.ExitCode == 0, "Unable to create junction fixture.");
            }

            string aliasToRealFile = Path.Combine(link, "found.exe");
            string aliasToMissingFile = Path.Combine(link, "missing.exe");
            Assert.False(_environment.IsSafeLocalPath(aliasToRealFile));
            Assert.False(_environment.IsSafeLocalPath(aliasToMissingFile));
            Assert.False(_environment.CheckFileIsTrulyMissing(aliasToMissingFile, out _, out _));

            Directory.Delete(actual, recursive: true); // The link now points to a missing target.
            Assert.False(_environment.IsSafeLocalPath(aliasToMissingFile));
            Assert.False(_environment.CheckFileIsTrulyMissing(aliasToMissingFile, out _, out _));
        }
        finally
        {
            // Directory.Exists is false for some dangling symlinks; remove the link directly.
            if (File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(link);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
