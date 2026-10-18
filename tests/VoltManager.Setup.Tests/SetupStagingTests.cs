using System.Security.AccessControl;
using System.Security.Principal;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class SetupStagingTests
{
    [Fact]
    public void Only_admins_or_system_are_trusted_owners_of_the_updates_root()
    {
        Assert.True(SetupStaging.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.True(SetupStaging.IsTrustedOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        // The interactive user could rewrite the DACL unelevated: ownership must move to Administrators.
        Assert.False(SetupStaging.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(SetupStaging.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-1001")));
        Assert.False(SetupStaging.IsTrustedOwner(null));
    }

    [Fact]
    public void Staged_update_path_must_be_a_file_directly_inside_one_child_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "ProgramData", "VoltManager", "Updates");
        string child = Path.Combine(root, "0123456789abcdef0123456789abcdef");

        Assert.True(SetupStaging.IsStagedUpdateExecutable(Path.Combine(child, "random.exe"), root));
        Assert.False(SetupStaging.IsStagedUpdateExecutable(Path.Combine(root, "random.exe"), root));
        Assert.False(SetupStaging.IsStagedUpdateExecutable(
            Path.Combine(child, "nested", "random.exe"),
            root));
        Assert.False(SetupStaging.IsStagedUpdateExecutable(
            Path.Combine(root + "-other", "child", "random.exe"),
            root));
    }

    [Fact]
    public void Junction_run_directory_cannot_authorize_deferred_deletion_of_external_executable()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "VoltManagerStagingPathTests", Guid.NewGuid().ToString("N"));
        string updatesRoot = Path.Combine(sandbox, "Updates");
        string unrelated = Path.Combine(sandbox, "unrelated");
        string runDirectory = Path.Combine(updatesRoot, Guid.NewGuid().ToString("N"));
        string externalExecutable = Path.Combine(unrelated, "personal.exe");
        Directory.CreateDirectory(updatesRoot);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(externalExecutable, "sentinel");

        try
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + runDirectory + "\" \"" + unrelated + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            mklink!.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
            Assert.True(SetupStaging.IsReparsePoint(runDirectory));

            // The path looks like a staging child, but resolves to an unrelated file.
            // This validation must not authorize MoveFileEx(delete-on-reboot).
            Assert.False(SetupStaging.IsStagedUpdateExecutable(
                Path.Combine(runDirectory, "personal.exe"), updatesRoot));
            Assert.Equal("sentinel", File.ReadAllText(externalExecutable));
        }
        finally
        {
            try { if (Directory.Exists(runDirectory)) Directory.Delete(runDirectory, false); } catch { }
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    [Fact]
    public void Protected_acl_disables_inheritance_and_grants_only_admins_and_system()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "VoltManagerAclTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DirectorySecurity original = Directory.GetAccessControl(root);

        try
        {
            if (!IsElevated())
            {
                // Unelevated the owner cannot be handed to Administrators: the call must fail closed.
                Assert.ThrowsAny<Exception>(() => SetupStaging.ApplyProtectedAcl(root));
                return;
            }

            SetupStaging.ApplyProtectedAcl(root);
            Assert.Equal(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                Directory.GetAccessControl(root, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));

            DirectorySecurity actual = Directory.GetAccessControl(root);
            Assert.True(actual.AreAccessRulesProtected);
            var rules = actual.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    targetType: typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();

            Assert.Equal(2, rules.Length);
            Assert.All(rules, rule =>
            {
                Assert.False(rule.IsInherited);
                Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
            });

            string[] sids = rules
                .Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            string[] expected =
            {
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            };
            Array.Sort(expected, StringComparer.Ordinal);
            Assert.Equal(expected, sids);
        }
        finally
        {
            try { Directory.SetAccessControl(root, original); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Junction_updates_root_is_rejected_before_anything_is_staged()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "VoltManagerJunctionTests", Guid.NewGuid().ToString("N"));
        string target = Path.Combine(sandbox, "target");
        string root = Path.Combine(sandbox, "Updates");
        Directory.CreateDirectory(target);
        try
        {
            using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + root + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }))
            {
                mklink!.WaitForExit();
                Assert.Equal(0, mklink.ExitCode);
            }
            Assert.True(SetupStaging.IsReparsePoint(root));

            Assert.Throws<InvalidDataException>(() => SetupStaging.CreateProtectedRunDirectory(root));
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, false); } catch { }
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    [Fact]
    public void Junction_above_updates_root_is_rejected_without_creating_or_resecuring_directories()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "VoltManagerStagingAncestryTests", Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(sandbox, "outside");
        string alias = Path.Combine(sandbox, "alias");
        string updatesRoot = Path.Combine(alias, "VoltManager", "Updates");
        Directory.CreateDirectory(outside);

        try
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + alias + "\" \"" + outside + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            mklink!.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);

            Assert.Throws<InvalidDataException>(() => SetupStaging.CreateProtectedRunDirectory(updatesRoot));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            try { if (Directory.Exists(alias)) Directory.Delete(alias, false); } catch { }
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    [Fact]
    public void Deferred_cleanup_rejects_nested_junction_without_scheduling_or_removing_external_content()
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "VoltManagerStagingCleanupTests", Guid.NewGuid().ToString("N"));
        string run = Path.Combine(sandbox, "run");
        string outside = Path.Combine(sandbox, "outside");
        string junction = Path.Combine(run, "nested-junction");
        string externalFile = Path.Combine(outside, "personal.txt");
        Directory.CreateDirectory(run);
        Directory.CreateDirectory(outside);
        File.WriteAllText(externalFile, "unrelated sentinel");

        try
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            mklink!.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);

            // Unlike Directory.EnumerateFiles(..., AllDirectories), the cleanup preflight
            // must find the junction without descending into the external target.
            Assert.True(SetupStaging.ContainsReparsePointInTree(run));
            var warnings = new List<string>();
            SetupStaging.DeleteRunDirectoryBestEffort(run, warnings.Add);

            Assert.True(Directory.Exists(run)); // fail closed, never call MoveFileEx
            Assert.Contains(warnings, warning => warning.IndexOf("reparse point", StringComparison.OrdinalIgnoreCase) >= 0);
            Assert.Equal("unrelated sentinel", File.ReadAllText(externalFile));
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction, false); } catch { }
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
