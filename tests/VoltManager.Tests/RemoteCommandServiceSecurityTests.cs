using System.Security.AccessControl;
using System.Security.Principal;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class RemoteCommandServiceSecurityTests
{
    [Fact]
    public void Event_security_grants_current_user_and_excludes_authenticated_users()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
        SecurityIdentifier authenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
        string name = "VoltManager_Test_EventAcl_" + Guid.NewGuid().ToString("N");
        using var evt = EventWaitHandleAcl.Create(
            false,
            EventResetMode.AutoReset,
            name,
            out _,
            RemoteCommandService.CreateEventSecurity());
        EventWaitHandleSecurity security = evt.GetAccessControl();
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<EventWaitHandleAccessRule>()
            .ToArray();

        EventWaitHandleAccessRule userRule = Assert.Single(rules.Where(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            currentUser.Equals(rule.IdentityReference)));
        Assert.Equal(
            EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize,
            userRule.EventWaitHandleRights & (EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize));
        Assert.DoesNotContain(rules, rule => authenticatedUsers.Equals(rule.IdentityReference));
    }
}
