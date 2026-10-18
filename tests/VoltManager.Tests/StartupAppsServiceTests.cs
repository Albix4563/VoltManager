using System.IO;
using Microsoft.Win32;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class StartupAppsServiceTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    [Fact]
    public void Enumerates_native_and_WOW64_HKLM_Run_without_losing_duplicate_names()
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, Run, "Shared", @"C:\Native\app.exe");
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry32, Run, "Shared", @"C:\X86\app.exe");
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun, "Shared", new byte[] { 0x03 });
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun32, "Shared", new byte[] { 0x07 });

        var snapshot = CreateService(registry).GetStartupApps();

        Assert.Empty(snapshot.Enabled);
        Assert.Equal(2, snapshot.Disabled.Count);
        Assert.Equal(2, snapshot.Disabled.Select(entry => entry.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(snapshot.Disabled, entry => entry.Id == "HKLM Run:Shared" && entry.Path == @"C:\Native\app.exe");
        Assert.Contains(snapshot.Disabled, entry => entry.Id == "HKLM Run32:Shared" && entry.Path == @"C:\X86\app.exe");
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x07, false)]
    public void Recognizes_all_documented_StartupApproved_state_variants(byte state, bool expectedEnabled)
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, Run, "Item", @"C:\Apps\item.exe");
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun, "Item", new[] { state });

        var snapshot = CreateService(registry).GetStartupApps();
        var entry = Assert.Single(snapshot.Enabled.Concat(snapshot.Disabled));

        Assert.Equal(expectedEnabled, entry.Enabled);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unknown")]
    public void Missing_empty_or_unknown_StartupApproved_state_defaults_to_enabled(string approvalState)
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, Run, "Item", @"C:\Apps\item.exe");
        if (approvalState != "missing")
        {
            byte[] state = approvalState == "empty" ? Array.Empty<byte>() : new byte[] { 0xFF };
            registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun, "Item", state);
        }

        var snapshot = CreateService(registry).GetStartupApps();

        Assert.Empty(snapshot.Disabled);
        var entry = Assert.Single(snapshot.Enabled);
        Assert.Equal("HKLM Run:Item", entry.Id);
        Assert.True(entry.Enabled);
    }

    [Fact]
    public void Toggles_only_WOW64_StartupApproved_Run32_for_duplicate_name()
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, Run, "Shared", @"C:\Native\app.exe");
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry32, Run, "Shared", @"C:\X86\app.exe");

        var service = CreateService(registry);
        Assert.Equal(2, service.GetStartupApps().Enabled.Count);

        bool changed = service.SetStartupAppEnabled("HKLM Run32:Shared", enabled: false);

        Assert.True(changed);
        var write = Assert.Single(registry.Writes);
        Assert.Equal(RegistryHive.LocalMachine, write.Hive);
        Assert.Equal(RegistryView.Registry64, write.View);
        Assert.Equal(ApprovedRun32, write.KeyPath);
        Assert.Equal("Shared", write.ValueName);
        Assert.Equal(RegistryValueKind.Binary, write.Kind);
        Assert.Equal((byte)0x03, write.State[0]);

        var snapshot = service.GetStartupApps();
        Assert.Equal("HKLM Run:Shared", Assert.Single(snapshot.Enabled).Id);
        Assert.Equal("HKLM Run32:Shared", Assert.Single(snapshot.Disabled).Id);
    }

    [Fact]
    public void Denied_approval_write_propagates_instead_of_reporting_success()
    {
        var registry = new FakeStartupRegistry { DenyWrite = true };
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, Run, "Protected", @"C:\Apps\protected.exe");

        Assert.Throws<UnauthorizedAccessException>(() =>
            CreateService(registry).SetStartupAppEnabled("HKLM Run:Protected", enabled: false));
    }

    [Theory]
    [InlineData((byte)0x02, true)]
    [InlineData((byte)0x03, false)]
    [InlineData((byte)0x06, true)]
    [InlineData((byte)0x07, false)]
    public void Idempotent_toggle_preserves_exact_existing_approval_metadata(byte state, bool enabled)
    {
        byte[] original = { state, 0xAB, 0xCD, 0xEF, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 };
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry32, Run, "Shared", @"C:\X86\shared.exe");
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun32, "Shared", original);

        Assert.True(CreateService(registry).SetStartupAppEnabled("HKLM Run32:Shared", enabled));

        Assert.Empty(registry.Writes);
        Assert.Equal(original, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun32, "Shared")));
        Assert.Equal(RegistryValueKind.Binary, registry.ReadKind(
            RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun32, "Shared"));
    }

    [Theory]
    [InlineData((byte)0x06, false)]
    [InlineData((byte)0x07, true)]
    public void Extended_approval_transition_is_rejected_without_guessing_encoding(byte state, bool enabled)
    {
        byte[] original = { state, 0x31, 0x41, 0x59, 0x26, 0x53, 0x58, 0x97, 0x93, 0x23, 0x84, 0x62 };
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, "Foreign", @"C:\Apps\foreign.exe");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Foreign", original);
        var service = CreateService(registry);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            service.SetStartupAppEnabled("HKCU Run:Foreign", enabled));

        Assert.Contains("0x06", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(registry.Writes);
        Assert.Equal(original, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Foreign")));
        Assert.Equal(state == 0x06, Assert.Single(
            service.GetStartupApps().Enabled.Concat(service.GetStartupApps().Disabled)).Enabled);
    }

    [Theory]
    [InlineData((byte)0x02, false)]
    [InlineData((byte)0x03, true)]
    public void Successful_standard_toggle_requires_exact_persisted_approval_readback(byte oldState, bool enabled)
    {
        var registry = new FakeStartupRegistry { IgnoreApprovalWrites = true };
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, "Unchanged", @"C:\Apps\unchanged.exe");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Unchanged",
            new byte[] { oldState, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            CreateService(registry).SetStartupAppEnabled("HKCU Run:Unchanged", enabled));

        Assert.Contains("StartupApproved", failure.Message);
        Assert.Single(registry.Writes);
        Assert.Equal(oldState, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Unchanged"))[0]);
    }

    [Fact]
    public void Standard_toggle_rejects_readback_with_corrupted_trailing_metadata()
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, "Changed", @"C:\Apps\changed.exe");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Changed", new byte[] { 0x02 });
        registry.OnApprovalWriteSuccess = () =>
            registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun,
                "Changed", new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0xFF });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            CreateService(registry).SetStartupAppEnabled("HKCU Run:Changed", enabled: false));

        Assert.Contains("StartupApproved", failure.Message);
        Assert.Single(registry.Writes);
    }

    [Theory]
    [InlineData((byte)0x02, false, (byte)0x03)]
    [InlineData((byte)0x03, true, (byte)0x02)]
    public void Standard_approval_transitions_are_written_and_read_back(
        byte previous, bool enabled, byte expected)
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, Run,
            "Normal", @"C:\Apps\normal.exe");
        registry.Seed(RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun,
            "Normal", new byte[] { previous, 0xFF, 0x11, 0x22 });
        var service = CreateService(registry);

        Assert.True(service.SetStartupAppEnabled("HKLM Run:Normal", enabled));

        var write = Assert.Single(registry.Writes);
        Assert.Equal(RegistryHive.LocalMachine, write.Hive);
        Assert.Equal(RegistryView.Registry64, write.View);
        Assert.Equal(ApprovedRun, write.KeyPath);
        Assert.Equal("Normal", write.ValueName);
        Assert.Equal(RegistryValueKind.Binary, write.Kind);
        Assert.Equal(new byte[] { expected, 0, 0, 0, 0, 0, 0, 0 }, write.State);
        Assert.Equal(write.State, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.LocalMachine, RegistryView.Registry64, ApprovedRun, "Normal")));
        Assert.Equal(enabled, Assert.Single(
            service.GetStartupApps().Enabled.Concat(service.GetStartupApps().Disabled)).Enabled);
    }

    [Fact]
    public void Unknown_approval_kind_is_not_silently_overwritten_by_toggle()
    {
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run,
            "Private", @"C:\Apps\private.exe");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun,
            "Private", "user supplied approval data", RegistryValueKind.String);

        Assert.Throws<InvalidOperationException>(() =>
            CreateService(registry).SetStartupAppEnabled("HKCU Run:Private", enabled: false));

        Assert.Empty(registry.Writes);
        Assert.Equal("user supplied approval data", registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Private"));
        Assert.Equal(RegistryValueKind.String, registry.ReadKind(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, "Private"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Adding_managed_app_reports_failure_when_approval_is_denied(bool previouslyDisabled)
    {
        string directory = Path.Combine(Path.GetTempPath(), "voltmanager-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "item.cmd");
        File.WriteAllText(path, "@echo off\n");

        try
        {
            const string name = "Miliano's App - item";
            string command = "\"" + path + "\"";
            var registry = new FakeStartupRegistry();
            if (previouslyDisabled)
            {
                registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, command);
                registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name, new byte[] { 0x03 });
            }
            registry.DenyWrite = true;

            var service = new StartupAppsService(registry, _ => "", File.Exists);

            Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(path));
            Assert.Equal(previouslyDisabled ? 0 : 1, registry.RunWrites.Count);
            Assert.Empty(registry.Writes);
            Assert.Equal(previouslyDisabled ? command : null,
                registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
            var snapshot = service.GetStartupApps();
            if (previouslyDisabled)
            {
                Assert.Empty(snapshot.Enabled);
                Assert.Equal(name, Assert.Single(snapshot.Disabled).Id);
                Assert.Equal((byte)0x03,
                    Assert.IsType<byte[]>(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name))[0]);
            }
            else
            {
                Assert.Empty(snapshot.Enabled);
                Assert.Empty(snapshot.Disabled);
            }
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Approval_failure_does_not_delete_a_concurrently_replaced_Run_value()
    {
        var registry = new FakeStartupRegistry { DenyWrite = true };
        const string name = "Miliano's App - item";
        const string otherCommand = "\"C:\\External\\other.exe\"";
        registry.OnApprovalFailure = () =>
            registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, otherCommand);

        var service = new StartupAppsService(registry, _ => "", _ => true);

        var error = Assert.Throws<AggregateException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Contains(error.InnerExceptions, ex => ex is UnauthorizedAccessException);
        Assert.Contains(error.InnerExceptions, ex => ex is InvalidOperationException && ex.Message.Contains("modificata"));
        Assert.Equal(otherCommand,
            registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
    }

    [Fact]
    public void Existing_Run_name_with_unreadable_value_is_not_claimed_as_managed_app()
    {
        const string occupiedName = "Miliano's App - item";
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, occupiedName, null);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        StartupAppEntry added = service.AddManagedStartupApp(@"C:\Apps\item.cmd");

        Assert.Equal("Miliano's App - item (2)", added.Id);
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, occupiedName));
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, occupiedName));
        Assert.Equal("\"C:\\Apps\\item.cmd\"",
            registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, added.Id));
    }

    [Fact]
    public void Failed_compensation_reports_both_approval_and_rollback_errors()
    {
        var registry = new FakeStartupRegistry
        {
            DenyWrite = true,
            DenyConditionalDelete = true,
        };
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var error = Assert.Throws<AggregateException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Contains(error.InnerExceptions, ex => ex is UnauthorizedAccessException && ex.Message.Contains("approval"));
        Assert.Contains(error.InnerExceptions, ex => ex is UnauthorizedAccessException && ex.Message.Contains("rollback"));
        Assert.Equal("\"C:\\Apps\\item.cmd\"",
            registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, "Miliano's App - item"));
    }

    [Fact]
    public void Denied_managed_Run_write_does_not_change_existing_disabled_approval()
    {
        string directory = Path.Combine(Path.GetTempPath(), "voltmanager-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "item.cmd");
        File.WriteAllText(path, "@echo off\n");

        try
        {
            const string name = "Miliano's App - item";
            var registry = new FakeStartupRegistry { DenyRunWrite = true };
            registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, "\"C:\\Other\\item.cmd\"");
            registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name, new byte[] { 0x03 });
            var service = new StartupAppsService(registry, _ => "", File.Exists);

            Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(path));

            Assert.Empty(registry.RunWrites);
            Assert.Empty(registry.Writes);
            Assert.Equal(name, Assert.Single(service.GetStartupApps().Disabled).Id);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Adding_managed_app_with_approved_write_publishes_run_and_reports_enabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), "voltmanager-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "item.cmd");
        File.WriteAllText(path, "@echo off\n");

        try
        {
            var registry = new FakeStartupRegistry();
            var service = new StartupAppsService(registry, _ => "", File.Exists);

            StartupAppEntry added = service.AddManagedStartupApp(path);

            Assert.True(added.Enabled);
            Assert.True(added.IsManaged);
            Assert.Equal("Miliano's App - item", added.Id);
            Assert.Equal(path, added.Path);
            Assert.Single(registry.RunWrites);
            Assert.Single(registry.Writes);
            Assert.Equal((byte)0x02, registry.Writes[0].State[0]);
            Assert.Equal(added.Id, Assert.Single(service.GetStartupApps().Enabled).Id);
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void Repeat_add_of_already_enabled_managed_app_preserves_approval_metadata()
    {
        const string path = @"C:\Apps\item.cmd";
        const string name = "Miliano's App - item";
        var approval = new byte[] { 0x06, 0xFF, 0xA3, 0x47, 0x27, 0x00, 0x10, 0xFF, 0xC1, 0x02, 0x7F, 0x80 };
        var registry = new FakeStartupRegistry();
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, "\"" + path + "\"");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name, approval);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        StartupAppEntry first = service.AddManagedStartupApp(path);
        StartupAppEntry second = service.AddManagedStartupApp(path);

        Assert.Equal(name, first.Id);
        Assert.Equal(first, second);
        Assert.Empty(registry.RunWrites);
        Assert.Empty(registry.Writes);
        Assert.Equal(approval, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name)));
    }

    [Fact]
    public void Failed_Run_write_after_commit_reports_failure_without_unsafe_compensation()
    {
        const string path = @"C:\Apps\item.cmd";
        const string name = "Miliano's App - item";
        var registry = new FakeStartupRegistry { ThrowAfterRunWrite = true };
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var failure = Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(path));

        Assert.Contains("Run", failure.Message);
        // Even a matching value could have been replaced by another actor.
        // Without confirmed write completion we cannot safely delete it.
        Assert.Equal("\"" + path + "\"",
            registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name));
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void Successful_approval_must_not_report_success_if_Run_was_concurrently_replaced()
    {
        const string name = "Miliano's App - item";
        const string foreignCommand = "\"C:\\Other\\unrelated.exe\"";
        var registry = new FakeStartupRegistry();
        registry.OnApprovalWriteSuccess = () => registry.Seed(
            RegistryHive.CurrentUser, RegistryView.Default, Run, name, foreignCommand);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var failure = Assert.Throws<AggregateException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Contains(failure.InnerExceptions, ex => ex is InvalidOperationException &&
            ex.Message.Contains("Run"));
        Assert.Equal(foreignCommand, registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, Run, name));
    }

    [Fact]
    public void Approval_failure_after_mutation_restores_prior_metadata_for_existing_Run()
    {
        const string path = @"C:\Apps\item.cmd";
        const string name = "Miliano's App - item";
        byte[] original = { 0x03, 0x01, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB };
        var registry = new FakeStartupRegistry { ThrowAfterApprovalWrite = true };
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, "\"" + path + "\"");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name, original,
            RegistryValueKind.Binary);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(path));

        Assert.Empty(registry.RunWrites);
        Assert.Equal(original, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name)));
        Assert.Equal(RegistryValueKind.Binary, registry.ReadKind(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name));
    }

    [Fact]
    public void Approval_failure_after_mutation_cleans_new_Run_and_approval()
    {
        var registry = new FakeStartupRegistry { ThrowAfterApprovalWrite = true };
        var service = new StartupAppsService(registry, _ => "", _ => true);
        const string name = "Miliano's App - item";

        Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));

        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name));
    }

    [Fact]
    public void Concurrent_Run_creation_before_conditional_write_does_not_get_overwritten()
    {
        const string name = "Miliano's App - item";
        const string foreignCommand = "\"C:\\Other\\private.exe\"";
        var registry = new FakeStartupRegistry();
        registry.OnRunWriteBeforeCommit = () => registry.Seed(
            RegistryHive.CurrentUser, RegistryView.Default, Run, name, foreignCommand);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var error = Assert.Throws<InvalidOperationException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Contains("Run", error.Message);
        Assert.Equal(foreignCommand, registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Empty(registry.Writes);
        Assert.Empty(registry.RunWrites);
    }

    [Fact]
    public void Failed_Run_write_before_commit_must_not_delete_concurrent_equal_value()
    {
        const string name = "Miliano's App - item";
        const string path = @"C:\Apps\item.cmd";
        const string foreignCommand = "\"" + path + "\"";
        var registry = new FakeStartupRegistry();
        registry.OnRunWriteBeforeCommit = () =>
        {
            // Another actor owns this value; our accessor has not written anything.
            registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, foreignCommand);
            throw new UnauthorizedAccessException("Synthetic failure before Run commit.");
        };
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var error = Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(path));

        Assert.Contains("before Run commit", error.Message);
        Assert.Equal(foreignCommand, registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Empty(registry.RunWrites);
        Assert.Empty(registry.Writes);
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name));
    }

    [Fact]
    public void Equal_value_concurrent_Run_replacement_reports_approval_failure_without_claiming_ownership()
    {
        const string name = "Miliano's App - item";
        const string command = "\"C:\\Apps\\item.cmd\"";
        var registry = new FakeStartupRegistry { DenyWrite = true };
        registry.OnApprovalFailure = () => registry.Seed(
            RegistryHive.CurrentUser, RegistryView.Default, Run, name, command);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var error = Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Contains("approval", error.Message);
        Assert.Empty(registry.Writes);
        // Run was confirmed earlier, but a concurrent equal-value replacement
        // cannot be distinguished by command alone. Do not assert it was
        // safely deleted or that this failure left a complete rollback.
    }

    [Fact]
    public void Preexisting_Run_with_nonstring_kind_is_not_claimed_or_modified()
    {
        const string name = "Miliano's App - item";
        const string command = "\"C:\\Apps\\item.cmd\"";
        var approval = new byte[] { 0x03, 0x88, 0x32, 0x00, 0x11, 0xFF };
        var registry = new FakeStartupRegistry { DenyApprovalRestore = true };
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, command,
            RegistryValueKind.ExpandString);
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name, approval);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        Assert.Throws<InvalidOperationException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));

        Assert.Equal(command, registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Equal(RegistryValueKind.ExpandString, registry.ReadKind(
            RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Equal(approval, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name)));
        Assert.Empty(registry.RunWrites);
        Assert.Empty(registry.Writes);
    }

    [Fact]
    public void Denied_approval_compensation_reports_partial_state_without_false_success()
    {
        const string name = "Miliano's App - item";
        var registry = new FakeStartupRegistry
        {
            ThrowAfterApprovalWrite = true,
            DenyApprovalRestore = true,
        };
        var service = new StartupAppsService(registry, _ => "", _ => true);

        var failure = Assert.Throws<AggregateException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Contains(failure.InnerExceptions, ex => ex is UnauthorizedAccessException && ex.Message.Contains("after mutation"));
        Assert.Contains(failure.InnerExceptions, ex => ex is UnauthorizedAccessException && ex.Message.Contains("restore"));
        Assert.Null(registry.ReadValue(RegistryHive.CurrentUser, RegistryView.Default, Run, name));
        Assert.Equal((byte)0x02, Assert.IsType<byte[]>(registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name))[0]);
    }

    [Fact]
    public void Partial_approval_failure_restores_prior_nonbinary_registry_kind()
    {
        const string name = "Miliano's App - item";
        const string originalState = "User-owned approval metadata";
        var registry = new FakeStartupRegistry { ThrowAfterApprovalWrite = true };
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, Run, name, "\"C:\\Apps\\item.cmd\"");
        registry.Seed(RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name,
            originalState, RegistryValueKind.String);
        var service = new StartupAppsService(registry, _ => "", _ => true);

        Assert.Throws<UnauthorizedAccessException>(() => service.AddManagedStartupApp(@"C:\Apps\item.cmd"));
        Assert.Equal(originalState, registry.ReadValue(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name));
        Assert.Equal(RegistryValueKind.String, registry.ReadKind(
            RegistryHive.CurrentUser, RegistryView.Default, ApprovedRun, name));
    }

    private static StartupAppsService CreateService(IStartupRegistryAccessor registry)
        => new(registry, _ => "");

    private sealed class FakeStartupRegistry : IStartupRegistryAccessor
    {
        private readonly Dictionary<(RegistryHive Hive, RegistryView View, string KeyPath, string ValueName), object?> _values =
            new(new StartupRegistryKeyComparer());
        private readonly Dictionary<(RegistryHive Hive, RegistryView View, string KeyPath, string ValueName), RegistryValueKind> _kinds =
            new(new StartupRegistryKeyComparer());

        public bool DenyWrite { get; set; }
        public bool DenyRunWrite { get; set; }
        public bool DenyConditionalDelete { get; set; }
        public bool ThrowAfterRunWrite { get; set; }
        public bool ThrowAfterApprovalWrite { get; set; }
        public bool DenyApprovalRestore { get; set; }
        public bool IgnoreApprovalWrites { get; set; }
        public Action? OnApprovalWriteSuccess { get; set; }
        public Action? OnRunWriteBeforeCommit { get; set; }
        public Action? OnApprovalFailure { get; set; }
        public List<(RegistryHive Hive, RegistryView View, string KeyPath, string ValueName, byte[] State, RegistryValueKind Kind)> Writes { get; } = new();
        public List<(RegistryHive Hive, RegistryView View, string KeyPath, string ValueName, string Command)> RunWrites { get; } = new();

        public void Seed(RegistryHive hive, RegistryView view, string keyPath, string valueName, object? value,
            RegistryValueKind kind = RegistryValueKind.None)
        {
            var key = (hive, view, keyPath, valueName);
            _values[key] = value;
            _kinds[key] = kind == RegistryValueKind.None
                ? value is byte[] ? RegistryValueKind.Binary : RegistryValueKind.String : kind;
        }

        public RegistryValueKind? ReadKind(RegistryHive hive, RegistryView view, string keyPath, string valueName)
            => _kinds.TryGetValue((hive, view, keyPath, valueName), out var kind) ? kind : null;

        public StartupRegistryValue? ReadStoredValue(RegistryHive hive, RegistryView view, string keyPath, string valueName)
        {
            var key = (hive, view, keyPath, valueName);
            return _values.TryGetValue(key, out var value)
                ? new StartupRegistryValue(value is byte[] bytes ? bytes.ToArray() : value, _kinds[key])
                : null;
        }

        public IReadOnlyList<(string Name, object? Value)> ReadValues(RegistryHive hive, RegistryView view, string keyPath)
            => _values.Where(pair => pair.Key.Hive == hive && pair.Key.View == view && pair.Key.KeyPath == keyPath)
                .Select(pair => (pair.Key.ValueName, (object?)pair.Value)).ToList();

        public object? ReadValue(RegistryHive hive, RegistryView view, string keyPath, string valueName)
            => _values.TryGetValue((hive, view, keyPath, valueName), out var value) ? value : null;

        public void WriteValue(RegistryHive hive, RegistryView view, string keyPath, string valueName, byte[] state, RegistryValueKind kind)
        {
            if (DenyWrite)
            {
                OnApprovalFailure?.Invoke();
                throw new UnauthorizedAccessException("Synthetic denied approval write.");
            }

            Writes.Add((hive, view, keyPath, valueName, state, kind));
            if (IgnoreApprovalWrites) return;
            Seed(hive, view, keyPath, valueName, state, kind);
            if (ThrowAfterApprovalWrite)
                throw new UnauthorizedAccessException("Synthetic approval write failure after mutation.");
            OnApprovalWriteSuccess?.Invoke();
        }

        public bool TryWriteStringValueIfAbsent(RegistryHive hive, RegistryView view, string keyPath, string valueName, string command)
        {
            if (DenyRunWrite)
                throw new UnauthorizedAccessException("Synthetic denied Run write.");

            OnRunWriteBeforeCommit?.Invoke();
            if (_values.ContainsKey((hive, view, keyPath, valueName)))
                return false;
            RunWrites.Add((hive, view, keyPath, valueName, command));
            Seed(hive, view, keyPath, valueName, command);
            if (ThrowAfterRunWrite)
                throw new UnauthorizedAccessException("Synthetic Run write failure after mutation.");
            return true;
        }

        public bool TryRestoreApprovalIfMatches(RegistryHive hive, RegistryView view, string keyPath,
            string valueName, byte[] expectedState, StartupRegistryValue? original)
        {
            if (DenyApprovalRestore)
                throw new UnauthorizedAccessException("Synthetic denied approval restore.");
            var key = (hive, view, keyPath, valueName);
            StartupRegistryValue? current = ReadStoredValue(hive, view, keyPath, valueName);
            if (SameValue(current, original)) return true;
            if (current is not { Kind: RegistryValueKind.Binary, Value: byte[] bytes } ||
                !bytes.SequenceEqual(expectedState))
                return false;
            if (original == null)
            {
                _values.Remove(key);
                _kinds.Remove(key);
            }
            else
                Seed(hive, view, keyPath, valueName, original.Value.Value, original.Value.Kind);
            return true;
        }

        private static bool SameValue(StartupRegistryValue? current, StartupRegistryValue? original)
        {
            if (current == null || original == null) return current == null && original == null;
            if (current.Value.Kind != original.Value.Kind) return false;
            return (current.Value.Value, original.Value.Value) switch
            {
                (byte[] x, byte[] y) => x.SequenceEqual(y),
                (object x, object y) => x.Equals(y),
                (null, null) => true,
                _ => false,
            };
        }

        public bool TryDeleteStringValueIfMatches(RegistryHive hive, RegistryView view, string keyPath, string valueName, string expectedCommand)
        {
            if (DenyConditionalDelete)
                throw new UnauthorizedAccessException("Synthetic denied rollback delete.");

            var key = (hive, view, keyPath, valueName);
            if (!_values.TryGetValue(key, out var current))
                return true;

            if (current is not string command || !string.Equals(command, expectedCommand, StringComparison.Ordinal))
                return false;

            _values.Remove(key);
            _kinds.Remove(key);
            return true;
        }

        private sealed class StartupRegistryKeyComparer : IEqualityComparer<
            (RegistryHive Hive, RegistryView View, string KeyPath, string ValueName)>
        {
            public bool Equals(
                (RegistryHive Hive, RegistryView View, string KeyPath, string ValueName) x,
                (RegistryHive Hive, RegistryView View, string KeyPath, string ValueName) y)
                => x.Hive == y.Hive && x.View == y.View &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.KeyPath, y.KeyPath) &&
                   StringComparer.OrdinalIgnoreCase.Equals(x.ValueName, y.ValueName);

            public int GetHashCode((RegistryHive Hive, RegistryView View, string KeyPath, string ValueName) key)
                => HashCode.Combine(key.Hive, key.View,
                    StringComparer.OrdinalIgnoreCase.GetHashCode(key.KeyPath),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(key.ValueName));
        }
    }
}
