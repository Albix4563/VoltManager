using System;
using System.IO;
using System.Threading;
using Xunit;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class StandbyAutoCleanerServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "StandbyCleanerTests_" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public StandbyAutoCleanerServiceTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void CheckAndClean_PurgesWhenStandbyMemoryExceedsThreshold()
    {
        var settings = new SettingsService(SettingsPath);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 2.0;
        settings.Current.StandbyAutoCleaner.IntervalMinutes = 60;
        settings.Current.StandbyAutoCleaner.LastPurgedUtc = DateTime.UtcNow.AddMinutes(-10);

        var memory = new MemoryStatus { StandbyGb = 2.5 };
        bool purgeCalled = false;

        var service = new StandbyAutoCleanerService(
            settings,
            () => memory,
            () => { purgeCalled = true; return true; });

        service.CheckAndClean();

        Assert.True(purgeCalled);
        Assert.NotNull(settings.Current.StandbyAutoCleaner.LastPurgedUtc);
        Assert.True((DateTime.UtcNow - settings.Current.StandbyAutoCleaner.LastPurgedUtc.Value).TotalSeconds < 5);
    }

    [Fact]
    public void CheckAndClean_PurgesWhenIntervalElapsed()
    {
        var settings = new SettingsService(SettingsPath);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 5.0;
        settings.Current.StandbyAutoCleaner.IntervalMinutes = 30;
        settings.Current.StandbyAutoCleaner.LastPurgedUtc = DateTime.UtcNow.AddMinutes(-31);

        var memory = new MemoryStatus { StandbyGb = 1.0 };
        bool purgeCalled = false;

        var service = new StandbyAutoCleanerService(
            settings,
            () => memory,
            () => { purgeCalled = true; return true; });

        service.CheckAndClean();

        Assert.True(purgeCalled);
        Assert.NotNull(settings.Current.StandbyAutoCleaner.LastPurgedUtc);
        Assert.True((DateTime.UtcNow - settings.Current.StandbyAutoCleaner.LastPurgedUtc.Value).TotalSeconds < 5);
    }

    [Fact]
    public void CheckAndClean_DoesNotPurgeWhenDisabled()
    {
        var settings = new SettingsService(SettingsPath);
        settings.Current.StandbyAutoCleaner.Enabled = false;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 2.0;

        var memory = new MemoryStatus { StandbyGb = 3.0 };
        bool purgeCalled = false;

        var service = new StandbyAutoCleanerService(
            settings,
            () => memory,
            () => { purgeCalled = true; return true; });

        service.CheckAndClean();

        Assert.False(purgeCalled);
    }

    [Fact]
    public void CheckAndClean_DoesNotPurgeWhenUnderThresholdAndNotElapsed()
    {
        var settings = new SettingsService(SettingsPath);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 2.0;
        settings.Current.StandbyAutoCleaner.IntervalMinutes = 60;
        settings.Current.StandbyAutoCleaner.LastPurgedUtc = DateTime.UtcNow.AddMinutes(-10);

        var memory = new MemoryStatus { StandbyGb = 1.5 };
        bool purgeCalled = false;

        var service = new StandbyAutoCleanerService(
            settings,
            () => memory,
            () => { purgeCalled = true; return true; });

        service.CheckAndClean();

        Assert.False(purgeCalled);
    }

    [Fact]
    public void CheckAndClean_DoesNotRunConcurrently()
    {
        var settings = new SettingsService(SettingsPath);
        settings.Current.StandbyAutoCleaner.Enabled = true;
        settings.Current.StandbyAutoCleaner.ThresholdGb = 2.0;

        var memory = new MemoryStatus { StandbyGb = 3.0 };

        var purgeStarted = new ManualResetEventSlim(false);
        var purgeBlock = new ManualResetEventSlim(false);
        int purgeCount = 0;

        var service = new StandbyAutoCleanerService(
            settings,
            () => memory,
            () =>
            {
                Interlocked.Increment(ref purgeCount);
                purgeStarted.Set();
                purgeBlock.Wait(); // block inside purge to simulate slow execution
                return true;
            });

        // Trigger manual purge asynchronously
        var manualTask = System.Threading.Tasks.Task.Run(() => service.PurgeManual());

        // Wait for purge to start
        Assert.True(purgeStarted.Wait(TimeSpan.FromSeconds(2)));

        // Try to trigger CheckAndClean concurrently.
        // It should immediately return because the lock is held.
        service.CheckAndClean();

        // Release the first purge
        purgeBlock.Set();
        manualTask.Wait();

        // CheckAndClean should have exited immediately without calling the purger
        Assert.Equal(1, purgeCount);
    }

    [Fact]
    public void PurgeManual_UpdatesLastPurgedWithoutAutoCleanEvent()
    {
        var settings = new SettingsService(SettingsPath);
        bool purgeCalled = false;
        int eventCount = 0;

        var service = new StandbyAutoCleanerService(
            settings,
            () => new MemoryStatus { StandbyGb = 0.25 },
            () => { purgeCalled = true; return true; });
        service.AutoCleaned += _ => eventCount++;

        bool result = service.PurgeManual();

        Assert.True(result);
        Assert.True(purgeCalled);
        Assert.NotNull(settings.Current.StandbyAutoCleaner.LastPurgedUtc);
        Assert.True((DateTime.UtcNow - settings.Current.StandbyAutoCleaner.LastPurgedUtc.Value).TotalSeconds < 5);
        Assert.Equal(0, eventCount);
    }
}
