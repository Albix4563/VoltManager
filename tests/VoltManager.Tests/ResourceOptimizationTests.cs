using System.IO;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Performance;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class ResourceOptimizationTests
{
    [Fact]
    public void Standby_clean_rechecks_protection_after_a_slow_memory_read()
    {
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        settings.Current.StandbyAutoCleaner.Enabled = true;
        bool protectedSession = false;
        int reads = 0, purges = 0;
        using var cleaner = new StandbyAutoCleanerService(settings,
            () => { if (++reads == 2) protectedSession = true; return new MemoryStatus { InUsePct = 95, StandbyGb = 3 }; },
            () => { purges++; return true; }, () => protectedSession);
        cleaner.CheckAndClean(DateTime.UnixEpoch);
        cleaner.CheckAndClean(DateTime.UnixEpoch.AddSeconds(30));
        Assert.Equal(0, purges);
    }

    [Fact]
    public void Standby_auto_clean_requires_sustained_ram_pressure_and_standby_threshold()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new SettingsService(path);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 2;
        settings.Current.StandbyAutoCleaner.IntervalMinutes = 60;
        int purges = 0;
        var memory = new MemoryStatus { InUsePct = 93, StandbyGb = 3 };
        using var cleaner = new StandbyAutoCleanerService(settings, () => memory, () => { purges++; return true; });
        var t0 = DateTime.UnixEpoch;

        cleaner.CheckAndClean(t0);
        cleaner.CheckAndClean(t0.AddSeconds(29));
        Assert.Equal(0, purges);
        cleaner.CheckAndClean(t0.AddSeconds(30));
        Assert.Equal(1, purges);

        memory = memory with { InUsePct = 91 };
        cleaner.CheckAndClean(t0.AddMinutes(61));
        Assert.Equal(1, purges);
    }

    [Fact]
    public void Standby_auto_clean_resets_pressure_candidate_during_protected_session()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new SettingsService(path);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 1;
        bool protectedSession = false;
        int purges = 0;
        var memory = new MemoryStatus { InUsePct = 95, StandbyGb = 3 };
        using var cleaner = new StandbyAutoCleanerService(
            settings, () => memory, () => { purges++; return true; }, () => protectedSession);
        var t0 = DateTime.UnixEpoch;

        cleaner.CheckAndClean(t0);
        protectedSession = true;
        cleaner.CheckAndClean(t0.AddSeconds(20));
        protectedSession = false;
        cleaner.CheckAndClean(t0.AddSeconds(40));
        cleaner.CheckAndClean(t0.AddSeconds(69));
        Assert.Equal(0, purges);
        cleaner.CheckAndClean(t0.AddSeconds(70));
        Assert.Equal(1, purges);
    }

    [Fact]
    public void Standby_auto_clean_treats_interval_as_minimum_and_does_not_record_failure()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new SettingsService(path);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 1;
        settings.Current.StandbyAutoCleaner.IntervalMinutes = 60;
        settings.Current.StandbyAutoCleaner.LastPurgedUtc = DateTime.UnixEpoch;
        int attempts = 0;
        var memory = new MemoryStatus { InUsePct = 95, StandbyGb = 3 };
        using var cleaner = new StandbyAutoCleanerService(settings, () => memory, () => { attempts++; return false; });

        cleaner.CheckAndClean(DateTime.UnixEpoch.AddMinutes(1));
        cleaner.CheckAndClean(DateTime.UnixEpoch.AddMinutes(1).AddSeconds(30));
        Assert.Equal(0, attempts);

        cleaner.CheckAndClean(DateTime.UnixEpoch.AddMinutes(61));
        Assert.Equal(1, attempts);
        Assert.Equal(DateTime.UnixEpoch, settings.Current.StandbyAutoCleaner.LastPurgedUtc);
    }

    [Fact]
    public void Manual_standby_clean_remains_available_during_protected_session()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var settings = new SettingsService(path);
        int purges = 0;
        using var cleaner = new StandbyAutoCleanerService(
            settings,
            () => new MemoryStatus(),
            () => { purges++; return true; },
            () => true);

        Assert.True(cleaner.PurgeManual());
        Assert.Equal(1, purges);
        Assert.NotNull(settings.Current.StandbyAutoCleaner.LastPurgedUtc);
    }

    [Fact]
    public void Process_sampling_is_on_demand_throttled_and_shared_by_concurrent_readers()
    {
        var now = DateTime.UnixEpoch;
        int captures = 0;
        var sampler = new ProcessMetricsSampler(() =>
        {
            Interlocked.Increment(ref captures);
            return new ProcessSnapshot(now, new[] { Process(1, "App", 0) });
        }, 4);

        Assert.Equal(0, captures);
        Assert.Empty(sampler.Read(8, Timeout.InfiniteTimeSpan, now));
        Parallel.For(0, 100, _ => sampler.Read(8, TimeSpan.FromSeconds(10), now));
        Assert.Equal(1, captures);
        sampler.Read(8, TimeSpan.FromSeconds(10), now.AddSeconds(9));
        sampler.Read(8, Timeout.InfiniteTimeSpan, now.AddMinutes(1));
        Assert.Equal(1, captures);
        now = now.AddSeconds(10);
        sampler.Read(8, TimeSpan.FromSeconds(10), now);
        Assert.Equal(2, captures);
    }

    [Fact]
    public void Process_deltas_preserve_grouping_and_ignore_reused_pids_and_duplicate_snapshots()
    {
        var now = DateTime.UnixEpoch;
        var processes = new[] { Process(1, "App", 10), Process(2, "app", 20) };
        var sampler = new ProcessMetricsSampler(() => new ProcessSnapshot(now, processes), 4);
        Assert.Equal(0, Assert.Single(sampler.Read(8, TimeSpan.Zero, now)).CpuPercent);
        now = now.AddSeconds(2);
        processes = new[] { Process(1, "App", 12), Process(2, "app", 22) };
        var row = Assert.Single(sampler.Read(8, TimeSpan.Zero, now));
        Assert.Equal(50, row.CpuPercent);
        Assert.Equal(2, row.Instances);
        Assert.Equal(200, row.RamMb);
        Assert.Equal(50, Assert.Single(sampler.Read(8, TimeSpan.Zero, now)).CpuPercent);

        now = now.AddSeconds(2);
        processes = new[] { Process(1, "App", 100) with { StartTimeUtc = now } };
        Assert.Equal(0, Assert.Single(sampler.Read(8, TimeSpan.Zero, now)).CpuPercent);
        now = now.AddMinutes(5);
        Assert.Equal(0, Assert.Single(sampler.Read(8, TimeSpan.Zero, now)).CpuPercent);
    }

    [Fact]
    public void Failed_process_capture_retains_cache_and_waits_before_retrying()
    {
        var now = DateTime.UnixEpoch;
        int captures = 0;
        var sampler = new ProcessMetricsSampler(() => ++captures == 1
            ? new ProcessSnapshot(now, new[] { Process(1, "App", 0) })
            : throw new IOException("unavailable"), 4);
        sampler.Read(8, TimeSpan.FromSeconds(3), now);
        Assert.Throws<IOException>(() => sampler.Read(8, TimeSpan.FromSeconds(3), now.AddSeconds(3)));
        Assert.Single(sampler.Read(8, TimeSpan.FromSeconds(3), now.AddSeconds(4)));
        Assert.Equal(2, captures);
    }

    [Fact]
    public void Slow_ui_keeps_one_pending_metrics_callback_and_the_latest_value()
    {
        var publisher = new UiMetricsPublisher();
        var queue = new Queue<Action>();
        var published = new List<object>();
        for (int i = 0; i < 10000; i++) publisher.QueueLatest(i, queue.Enqueue, published.Add);
        Assert.Single(queue);
        Assert.Empty(published);
        queue.Dequeue()();
        Assert.Equal(new object[] { 9999 }, published);
        publisher.QueueLatest(10000, queue.Enqueue, value =>
        {
            published.Add(value);
            publisher.QueueLatest(10001, queue.Enqueue, published.Add);
        });
        queue.Dequeue()();
        queue.Dequeue()();
        Assert.Equal(new object[] { 9999, 10000, 10001 }, published);
    }

    [Fact]
    public void Metrics_dispatch_can_recover_after_the_ui_rejects_a_callback()
    {
        var publisher = new UiMetricsPublisher();
        Assert.Throws<InvalidOperationException>(() => publisher.QueueLatest(1,
            _ => throw new InvalidOperationException(), _ => { }));
        object? published = null;
        publisher.QueueLatest(2, action => action(), value => published = value);
        Assert.Equal(2, published);
    }

    [Fact]
    public void Widget_payloads_only_contain_the_metrics_the_widget_renders()
    {
        var metrics = new MetricsSnapshot { Cpu = 50, GpuAvailable = false, CpuTemp = null, GpuTemp = 60 };
        foreach (string type in new[] { "clock", "calendar", "power", "plans" })
            Assert.Null(WidgetWindow.MetricsPayload(type, metrics));
        using var usage = JsonDocument.Parse(JsonSerializer.Serialize(WidgetWindow.MetricsPayload("usage", metrics)));
        Assert.Equal(5, usage.RootElement.EnumerateObject().Count());
        Assert.Equal(50, usage.RootElement.GetProperty("cpu").GetDouble());
        Assert.False(usage.RootElement.GetProperty("gpuAvailable").GetBoolean());
        using var temps = JsonDocument.Parse(JsonSerializer.Serialize(WidgetWindow.MetricsPayload("temps", metrics)));
        Assert.Equal(2, temps.RootElement.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, temps.RootElement.GetProperty("cpuTemp").ValueKind);
        Assert.Equal(60, temps.RootElement.GetProperty("gpuTemp").GetDouble());
    }

    private static ProcessSample Process(int pid, string name, int cpuSeconds) =>
        new(pid, 0, name, 100 * 1024 * 1024, TimeSpan.FromSeconds(cpuSeconds), DateTime.UnixEpoch);

    [Fact]
    public async Task App_profile_refresh_never_overlaps_a_running_scan_and_recovers_after_it()
    {
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        settings.Current.AppPowerProfiles.Enabled = true;
        using var service = new AppPowerProfileService(settings);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.ActivityChanged += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var running = Task.Run(service.Refresh);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            settings.Current.AppPowerProfiles.Enabled = false;
            Assert.True(service.Refresh().Enabled);
        }
        finally { release.Set(); }
        await running;
        Assert.False(service.Refresh().Enabled);
    }

    [Fact]
    public async Task Heavy_app_refresh_never_overlaps_a_running_scan_and_recovers_after_it()
    {
        var settings = new SettingsService(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json"));
        settings.Current.HeavyAppDetection.Enabled = false;
        settings.Current.HeavyAppDetection.TargetPlan = PlanId.Balanced;
        using var service = new HeavyAppDetectionService(settings);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        service.ActivityChanged += _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); };
        var running = Task.Run(service.Refresh);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            settings.Current.HeavyAppDetection.TargetPlan = PlanId.PowerSaver;
            Assert.Equal(PlanId.Balanced, service.Refresh().TargetPlan);
        }
        finally { release.Set(); }
        await running;
        Assert.Equal(PlanId.PowerSaver, service.Refresh().TargetPlan);
    }

    [Fact]
    public void App_profile_process_name_prefilter_skips_impossible_executables()
    {
        var rule = new AppPowerProfileRule { Path = @"C:\Apps\Code.exe", Enabled = true };
        var rules = new Dictionary<string, AppPowerProfileRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["Code.exe"] = rule,
        };

        Assert.True(AppPowerProfileService.CouldMatchProcessName("Code", rules));
        Assert.True(AppPowerProfileService.CouldMatchProcessName("Code.exe", rules));
        Assert.True(AppPowerProfileService.CouldMatchProcessName("", rules));
        Assert.False(AppPowerProfileService.CouldMatchProcessName("chrome", rules));
    }

    [Fact]
    public void Game_detection_reuses_gpu_preferences_for_thirty_seconds()
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var service = new HeavyAppDetectionService(new SettingsService(settingsPath));
        int reads = 0;
        HashSet<string> Reader()
        {
            reads++;
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Games\Game.exe" };
        }

        var t0 = DateTime.UnixEpoch;
        var first = service.GetCachedGpuPreferences(t0, Reader);
        var cached = service.GetCachedGpuPreferences(t0.AddSeconds(29), Reader);
        var refreshed = service.GetCachedGpuPreferences(t0.AddSeconds(30), Reader);

        Assert.Same(first, cached);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, reads);
    }
}
