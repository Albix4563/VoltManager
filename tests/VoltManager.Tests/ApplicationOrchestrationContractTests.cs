using System.IO;

namespace VoltManager.Tests;

public sealed class ApplicationOrchestrationContractTests
{
    [Fact]
    public void App_no_longer_declares_coordinator_owned_timer_fields()
    {
        string source = LocateSource("App.xaml.cs");

        Assert.DoesNotContain("_planPollTimer", source);
        Assert.DoesNotContain("_batteryHistoryTimer", source);
    }

    [Fact]
    public void MainWindow_no_longer_declares_update_or_tray_timer_fields()
    {
        string source = LocateSource("MainWindow.xaml.cs");

        Assert.DoesNotContain("_autoUpdateTimer", source);
        Assert.DoesNotContain("_trayTeardownTimer", source);
    }

    [Fact]
    public void App_no_longer_owns_power_arbitration_state_or_policy_handlers()
    {
        string source = LocateSource("App.xaml.cs");

        string[] coordinatorOwnedMembers =
        {
            "_automationTickRunning",
            "_currentSamplingInterval",
            "_heavyAppPlanSessionActive",
            "_planBeforeHeavyAppSession",
            "_appProfilePlanSessionActive",
            "_planBeforeAppProfileSession",
            "_appProfileKeepAwakeRequested",
            "_planGuard",
            "_lastPublishedPlanReason",
            "_fallbackPlanReason",
            "private void PublishCpuAutomationState",
            "private bool HandlePowerSourcePlans",
            "private bool HandleThermalGuard",
            "private bool HandleIdlePowerGuard",
            "private bool HandleAppPowerProfiles",
            "private bool HandleHeavyAppDetection",
            "private PowerPlan? ReassertExpectedPlanIfNeeded",
            "private void ClearExpiredManualOverride",
        };

        foreach (string member in coordinatorOwnedMembers)
            Assert.DoesNotContain(member, source);
    }

    [Fact]
    public void MainWindow_runtime_is_stopped_before_application_services_are_disposed()
    {
        string appSource = LocateSource("App.xaml.cs");
        string windowSource = LocateSource("MainWindow.xaml.cs");

        Assert.Contains("internal void StopRuntime()", windowSource);
        int stopWindow = appSource.IndexOf("_mainWindow?.StopRuntime()", StringComparison.Ordinal);
        int disposeServices = appSource.IndexOf("DisposeApplicationServices", StringComparison.Ordinal);
        Assert.True(stopWindow >= 0, "App exit must stop the MainWindow runtime explicitly");
        Assert.True(disposeServices >= 0, "App exit must dispose application services");
        Assert.True(stopWindow < disposeServices, "MainWindow runtime must stop before application services");
    }

    private static string LocateSource(string fileName)
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string candidate = Path.Combine(dir, "src", "VoltManager", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(
            "Could not locate " + fileName + " from " + AppContext.BaseDirectory);
    }
}
