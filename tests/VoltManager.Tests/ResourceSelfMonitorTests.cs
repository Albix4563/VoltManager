using VoltManager.Reliability;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class ResourceSelfMonitorTests
{
    [Fact]
    public void Scheduler_uses_five_minute_cadence()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        int samples = 0;
        using var monitor = new ResourceSelfMonitor(
            () => Sample(clock.UtcNow, handles: ++samples),
            scheduler,
            () => clock.UtcNow,
            QuietOptions());

        Assert.Equal(TimeSpan.FromMinutes(5), scheduler.DueTime);
        Assert.Equal(TimeSpan.FromMinutes(5), scheduler.Period);
        Assert.Equal(0, samples);

        clock.Advance(TimeSpan.FromMinutes(5));
        scheduler.Fire();
        Assert.Equal(1, samples);
    }

    [Fact]
    public void Rolling_window_is_bounded_to_48_samples()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        int next = 0;
        using var monitor = new ResourceSelfMonitor(
            () => Sample(clock.UtcNow, handles: next++),
            scheduler,
            () => clock.UtcNow,
            QuietOptions());

        for (int i = 0; i < 80; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            scheduler.Fire();
        }

        Assert.Equal(48, monitor.WindowCount);
        Assert.Equal(32, monitor.WindowSnapshot[0].HostHandleCount);
        Assert.Equal(79, monitor.WindowSnapshot[^1].HostHandleCount);
    }

    [Fact]
    public void Absolute_warning_is_rate_limited_and_recovery_logs_once()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        var infos = new List<string>();
        var warns = new List<string>();
        int handles = 25_000;
        using var monitor = new ResourceSelfMonitor(
            () => Sample(clock.UtcNow, handles: handles),
            scheduler,
            () => clock.UtcNow,
            QuietOptions() with { GdiObjectThreshold = 8_000, UserObjectThreshold = 8_000 },
            infos.Add,
            warns.Add,
            _ => { });

        scheduler.Fire();
        clock.Advance(TimeSpan.FromHours(5));
        scheduler.Fire();
        Assert.Single(warns);

        handles = 100;
        scheduler.Fire();
        scheduler.Fire();
        Assert.Single(infos.Where(message => message.Contains("condition cleared: handles", StringComparison.Ordinal)));
    }

    [Fact]
    public void Gdi_and_user_thresholds_log_error_not_warning()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        var warns = new List<string>();
        var errors = new List<string>();
        using var monitor = new ResourceSelfMonitor(
            () => Sample(clock.UtcNow, gdi: 8_001, user: 8_500),
            scheduler,
            () => clock.UtcNow,
            QuietOptions() with { GdiObjectThreshold = 8_000, UserObjectThreshold = 8_000 },
            _ => { },
            warns.Add,
            errors.Add);

        scheduler.Fire();

        Assert.Empty(warns);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Warning_can_repeat_after_six_hours_while_condition_remains_active()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        var warns = new List<string>();
        using var monitor = new ResourceSelfMonitor(
            () => Sample(clock.UtcNow, handles: 25_000),
            scheduler,
            () => clock.UtcNow,
            QuietOptions(),
            _ => { },
            warns.Add,
            _ => { });

        scheduler.Fire();
        clock.Advance(TimeSpan.FromHours(6));
        scheduler.Fire();

        Assert.Equal(2, warns.Count);
    }

    [Fact]
    public void Flat_noisy_series_does_not_trigger_growth()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        ResourceSelfSample[] samples = Enumerable.Range(0, 24)
            .Select(i => Sample(start.AddMinutes(i * 5), handles: 1_000 + ((i % 4) - 2) * 3))
            .ToArray();

        Assert.False(ResourceSelfMonitor.IsSustainedGrowth(samples, s => s.HostHandleCount, 12, 50));
    }

    [Fact]
    public void Steady_growth_triggers_growth_detection()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        ResourceSelfSample[] samples = Enumerable.Range(0, 24)
            .Select(i => Sample(start.AddMinutes(i * 5), handles: 1_000 + i * 10))
            .ToArray();

        Assert.True(ResourceSelfMonitor.IsSustainedGrowth(samples, s => s.HostHandleCount, 12, 50));
        Assert.InRange(ResourceSelfMonitor.LeastSquaresSlopePerHour(samples, s => s.HostHandleCount)!.Value, 119.9, 120.1);
    }

    [Fact]
    public void Mostly_increasing_series_tolerates_small_regressions()
    {
        DateTimeOffset start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        ResourceSelfSample[] samples = Enumerable.Range(0, 18)
            .Select(i => Sample(start.AddMinutes(i * 5), handles: 1_000 + i * 12 - (i % 5 == 0 ? 10 : 0)))
            .ToArray();

        Assert.True(ResourceSelfMonitor.IsSustainedGrowth(samples, s => s.HostHandleCount, 12, 50));
    }

    [Fact]
    public void Sampler_exception_is_isolated_and_later_samples_continue()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        int calls = 0;
        using var monitor = new ResourceSelfMonitor(
            () =>
            {
                calls++;
                if (calls == 1) throw new InvalidOperationException("expected");
                return Sample(clock.UtcNow, handles: calls);
            },
            scheduler,
            () => clock.UtcNow,
            QuietOptions());

        Exception? first = Record.Exception(scheduler.Fire);
        scheduler.Fire();

        Assert.Null(first);
        Assert.Equal(2, calls);
        Assert.Equal(1, monitor.WindowCount);
    }

    [Fact]
    public void Dispose_is_idempotent_concurrent_safe_and_blocks_future_ticks()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        int samples = 0;
        var monitor = new ResourceSelfMonitor(
            () => Sample(clock.UtcNow, handles: ++samples),
            scheduler,
            () => clock.UtcNow,
            QuietOptions());

        scheduler.Fire();
        Parallel.For(0, 64, _ => monitor.Dispose());
        scheduler.Fire();

        Assert.Equal(1, samples);
        Assert.True(scheduler.Disposed);
    }

    [Fact]
    public async Task Overlapping_ticks_are_single_flight()
    {
        var scheduler = new ManualScheduler();
        var clock = new FakeClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int samples = 0;
        using var monitor = new ResourceSelfMonitor(
            () =>
            {
                Interlocked.Increment(ref samples);
                entered.Set();
                release.Wait();
                return Sample(clock.UtcNow);
            },
            scheduler,
            () => clock.UtcNow,
            QuietOptions());

        Task first = Task.Run(scheduler.Fire);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        scheduler.Fire();
        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, samples);
    }

    [Fact]
    public void Lifecycle_creates_and_disposes_resource_monitor_per_epoch()
    {
        int created = 0;
        int disposed = 0;
        var actions = new ApplicationLifecycleActions(
            () => { }, () => { }, () => { }, () => { },
            _ => new CallbackDisposable(),
            _ => new CallbackDisposable(),
            CreateResourceSelfMonitor: _ => new CallbackDisposable(
                () => created++, () => disposed++));
        using var lifecycle = new ApplicationLifecycleCoordinator(actions);

        for (int i = 0; i < 100; i++)
        {
            lifecycle.Start();
            lifecycle.Stop();
        }

        Assert.Equal(100, created);
        Assert.Equal(100, disposed);
    }

    private static ResourceSelfMonitorOptions QuietOptions()
        => ResourceSelfMonitorOptions.Default with
        {
            SummaryInterval = TimeSpan.FromDays(1),
            HostPrivateBytesThreshold = long.MaxValue,
            ThreadCountThreshold = int.MaxValue,
            GdiObjectThreshold = int.MaxValue,
            UserObjectThreshold = int.MaxValue,
            HandleGrowthPerHour = double.MaxValue,
            PrivateBytesGrowthPerHour = double.MaxValue,
            GdiGrowthPerHour = double.MaxValue,
        };

    private static ResourceSelfSample Sample(
        DateTimeOffset timestamp,
        long privateBytes = 100,
        int handles = 100,
        int threads = 10,
        int gdi = 10,
        int user = 10)
        => new(timestamp, privateBytes, 100, handles, threads, gdi, user, 0, 0);

    private sealed class FakeClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        public void Advance(TimeSpan delta) => UtcNow += delta;
    }

    private sealed class ManualScheduler : IResourceSelfMonitorScheduler
    {
        private Action? _callback;
        public TimeSpan DueTime { get; private set; }
        public TimeSpan Period { get; private set; }
        public bool Disposed { get; private set; }

        public IDisposable SchedulePeriodic(TimeSpan dueTime, TimeSpan period, Action callback)
        {
            DueTime = dueTime;
            Period = period;
            _callback = callback;
            return new CallbackDisposable(onDispose: () => Disposed = true);
        }

        public void Fire()
        {
            if (!Disposed) _callback?.Invoke();
        }
    }
}
