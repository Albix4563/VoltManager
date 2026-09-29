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

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
