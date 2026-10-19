using System.Security.AccessControl;
using System.Security.Principal;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class UpdateSessionProtocolTests
{
    [Fact]
    public void Session_identifiers_reject_unexpected_shapes()
    {
        string id = UpdateSessionProtocol.NewId();
        Assert.True(UpdateSessionProtocol.TryValidateId(id, out string parsed));
        Assert.Equal(id, parsed);
        Assert.False(UpdateSessionProtocol.TryValidateId("", out _));
        Assert.False(UpdateSessionProtocol.TryValidateId(id + "a", out _));
        Assert.False(UpdateSessionProtocol.TryValidateId("../" + id, out _));
        Assert.False(UpdateSessionProtocol.TryValidateId(Guid.NewGuid().ToString("D"), out _));
    }

    [Fact]
    public void Theme_snapshot_roundtrips_only_ten_opaque_rgb_colors()
    {
        string[] palette =
        [
            "#0B1120", "#111827", "#1E293B", "#0A84FF", "#4DA3FF",
            "#1476D4", "#F8FAFC", "#CBD5E1", "#3B4561", "#FFFFFF"
        ];
        string encoded = UpdateSessionProtocol.EncodePalette(palette);
        string argumentLine = UpdateSessionProtocol.BuildArguments("/update --pid 123 --lang it",
            UpdateSessionProtocol.NewId(), encoded);
        Assert.Contains("--update-session \"", argumentLine);
        Assert.Contains("--update-palette \"" + encoded + "\"", argumentLine);
        Assert.True(UpdateSessionProtocol.TryDecodePalette(encoded, out string[] decoded));
        Assert.Equal(palette, decoded);
        Assert.False(UpdateSessionProtocol.TryDecodePalette("not-base64!", out _));
        Assert.False(UpdateSessionProtocol.TryDecodePalette(Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("[\"#000000\"]")), out _));
        Assert.Throws<ArgumentException>(() => UpdateSessionProtocol.EncodePalette(
            palette.Take(9).ToArray()));
        Assert.Throws<ArgumentException>(() => UpdateSessionProtocol.EncodePalette(
            palette.Select((color, index) => index == 4 ? "#A0C" : color).ToArray()));
    }

    [Fact]
    public async Task Named_session_events_enforce_handoff_order_and_isolation()
    {
        string id = UpdateSessionProtocol.NewId();
        using UpdateSessionChannels host = UpdateSessionProtocol.CreateHost(id);
        using UpdateSessionChannels setup = UpdateSessionProtocol.OpenSetup(id);

        Assert.False(await host.WaitVisibleAsync(TimeSpan.FromMilliseconds(10)));
        setup.SignalVisible();
        Assert.True(await host.WaitVisibleAsync(TimeSpan.FromSeconds(1)));

        Assert.False(await setup.WaitProceedAsync(TimeSpan.FromMilliseconds(10)));
        host.SignalProceed();
        Assert.True(await setup.WaitProceedAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await host.WaitAcceptedAsync(TimeSpan.FromMilliseconds(10)));
        setup.SignalAccepted();
        Assert.True(await host.WaitAcceptedAsync(TimeSpan.FromSeconds(1)));

        Assert.False(await setup.WaitReadyAsync(TimeSpan.FromMilliseconds(10)));
        Assert.True(UpdateSessionProtocol.TrySignalReady(id));
        Assert.True(await setup.WaitReadyAsync(TimeSpan.FromSeconds(1)));
        Assert.False(UpdateSessionProtocol.TrySignalReady(UpdateSessionProtocol.NewId()));
    }

    [Fact]
    public async Task Aborted_session_cannot_proceed()
    {
        string id = UpdateSessionProtocol.NewId();
        using UpdateSessionChannels host = UpdateSessionProtocol.CreateHost(id);
        using UpdateSessionChannels setup = UpdateSessionProtocol.OpenSetup(id);

        host.SignalAbort();
        Assert.False(await setup.WaitProceedAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await host.WaitAcceptedAsync(TimeSpan.FromSeconds(1)));
        Assert.False(await setup.WaitReadyAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Abort_takes_precedence_over_simultaneous_proceed()
    {
        string id = UpdateSessionProtocol.NewId();
        using UpdateSessionChannels host = UpdateSessionProtocol.CreateHost(id);
        using UpdateSessionChannels setup = UpdateSessionProtocol.OpenSetup(id);
        host.SignalProceed();
        setup.SignalAbort();
        Assert.True(setup.IsAborted);
        Assert.False(await setup.WaitProceedAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Event_acl_limits_access_to_current_user_administrators_and_system()
    {
        var rules = UpdateSessionProtocol.CreateEventSecurity()
            .GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<EventWaitHandleAccessRule>()
            .ToArray();
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        Assert.Contains(rules, rule => user.Equals(rule.IdentityReference)
            && (rule.EventWaitHandleRights &
                (EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
                == (EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize));
        Assert.DoesNotContain(rules, rule => rule.IdentityReference is SecurityIdentifier sid
            && sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid));
    }
}
