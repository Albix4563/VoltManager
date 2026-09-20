using System.IO;
using System.Reflection;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

[Collection("RuntimeServiceValidationEnvironment")]
public sealed class RuntimeServiceLifecycleTests
{
    private static object? Field(object instance, string name) =>
        instance.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(instance);

    [Fact]
    public void Heavy_app_detection_stop_is_idempotent_and_restartable()
    {
        using var service = new HeavyAppDetectionService(TestSettings.Create());
        service.StartDelayed(TimeSpan.FromHours(1));
        object? first = Field(service, "_timer");
        Assert.NotNull(first);

        service.Stop();
        service.Stop();
        Assert.Null(Field(service, "_timer"));

        service.StartDelayed(TimeSpan.FromHours(1));
        object? second = Field(service, "_timer");
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void App_profile_and_standby_cleaner_restart_after_stop()
    {
        using var profiles = new AppPowerProfileService(TestSettings.Create());
        using var cleaner = new StandbyAutoCleanerService(TestSettings.Create());

        profiles.StartDelayed(TimeSpan.FromHours(1));
        cleaner.StartDelayed(TimeSpan.FromHours(1));
        profiles.Stop();
        cleaner.Stop();

        Assert.Null(Field(profiles, "_timer"));
        Assert.Null(Field(cleaner, "_timer"));

        profiles.StartDelayed(TimeSpan.FromHours(1));
        cleaner.StartDelayed(TimeSpan.FromHours(1));
        Assert.NotNull(Field(profiles, "_timer"));
        Assert.NotNull(Field(cleaner, "_timer"));
    }

    [Fact]
    public void Monitor_timer_is_recreated_after_stop()
    {
        using var monitor = new MonitorService();
        monitor.Start(TimeSpan.FromHours(1));
        object? first = Field(monitor, "_timer");
        Assert.NotNull(first);

        monitor.Stop();
        monitor.Stop();
        Assert.Null(Field(monitor, "_timer"));

        monitor.Start(TimeSpan.FromHours(1));
        object? second = Field(monitor, "_timer");
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Scheduled_power_daily_timer_is_recreated_after_stop()
    {
        var settings = TestSettings.Create();
        settings.Update(state =>
        {
            state.AutoShutdown.Enabled = true;
            state.AutoShutdown.Mode = ScheduledPowerMode.Daily;
            state.AutoShutdown.Time = "23:59";
        });
        using var service = new ScheduledPowerActionService(
            settings, new NoopPowerActionExecutor(), new FixedClock());

        service.Start();
        object? first = Field(service, "_dailyTimer");
        Assert.NotNull(first);

        service.Stop();
        service.Stop();
        Assert.Null(Field(service, "_dailyTimer"));

        service.Start();
        object? second = Field(service, "_dailyTimer");
        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Fullscreen_coverage_stop_unhooks_and_allows_restart()
    {
        using var coverage = new ProtectedFullscreenCoverageService(
            () => new HashSet<int>());

        coverage.Start();
        coverage.Stop();
        coverage.Stop();

        var hooks = Assert.IsAssignableFrom<System.Collections.ICollection>(
            Field(coverage, "_hooks"));
        Assert.Empty(hooks.Cast<object>());

        coverage.Start();
        coverage.Stop();
    }

    [Fact]
    public void Remote_commands_start_is_idempotent_and_restartable()
    {
        string? previousRoot = Environment.GetEnvironmentVariable(ValidationEnvironment.RootVariable);
        string validationRoot = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, validationRoot);
            using var remote = new RemoteCommandService();
            remote.Start();
            int firstCount = ((System.Collections.ICollection)Field(remote, "_waits")!).Count;
            Assert.Equal(RemoteCommandProtocol.AllKeys.Length, firstCount);

            remote.Start();
            Assert.Equal(firstCount,
                ((System.Collections.ICollection)Field(remote, "_waits")!).Count);

            remote.Stop();
            remote.Stop();
            Assert.Empty(((System.Collections.ICollection)Field(remote, "_waits")!).Cast<object>());

            remote.Start();
            Assert.Equal(firstCount,
                ((System.Collections.ICollection)Field(remote, "_waits")!).Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ValidationEnvironment.RootVariable, previousRoot);
        }
    }

    private sealed class NoopPowerActionExecutor : IPowerActionExecutor
    {
        public void Execute(ScheduledPowerActionType action) { }
    }

    private sealed class FixedClock : ISystemClock
    {
        public DateTime UtcNow => DateTime.UnixEpoch;
    }
}

[CollectionDefinition("RuntimeServiceValidationEnvironment", DisableParallelization = true)]
public sealed class RuntimeServiceValidationEnvironmentCollection;
