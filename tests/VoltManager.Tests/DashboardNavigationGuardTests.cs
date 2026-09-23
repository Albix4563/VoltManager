using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class DashboardNavigationGuardTests
{
    [Fact]
    public void Expected_bootstrap_cancellation_is_not_a_dashboard_failure()
    {
        var guard = new DashboardNavigationGuard();
        guard.ExpectCancellation(41);

        Assert.False(guard.IsFailure(41, isSuccess: false));
        Assert.True(guard.IsFailure(42, isSuccess: false));
    }

    [Fact]
    public void Successful_dashboard_navigation_is_not_a_failure()
    {
        var guard = new DashboardNavigationGuard();

        Assert.False(guard.IsFailure(43, isSuccess: true));
    }

    [Fact]
    public void Superseded_navigation_is_not_a_dashboard_failure()
    {
        var guard = new DashboardNavigationGuard();

        Assert.False(guard.IsFailure(44, isSuccess: false, operationCanceled: true));
    }

    [Fact]
    public void Pending_app_navigation_prevents_duplicate_navigation_from_restore()
    {
        var guard = new DashboardNavigationGuard();
        guard.BeginAppNavigation();

        Assert.False(guard.CanStartAppNavigation(webViewReady: true, source: "about:blank"));

        guard.EndAppNavigation();
        Assert.True(guard.CanStartAppNavigation(webViewReady: true, source: "about:blank"));
    }
}
