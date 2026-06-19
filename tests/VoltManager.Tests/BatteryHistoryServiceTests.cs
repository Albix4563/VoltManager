using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class BatteryHistoryServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "BatteryHistoryTests_" + Guid.NewGuid().ToString("N"));
    private string HistoryPath => Path.Combine(_dir, "battery-history.json");

    public BatteryHistoryServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static BatteryHistorySample Sample(long t, int pct) =>
        new() { T = t, Pct = pct };

    [Fact]
    public void Append_RejectsSampleWithinMinInterval()
    {
        var list = new List<BatteryHistorySample> { Sample(1000, 80) };

        bool added = BatteryHistoryService.Append(list, Sample(1030, 79), capacity: 100, minInterval: TimeSpan.FromMinutes(1));

        Assert.False(added);
        Assert.Single(list);
    }

    [Fact]
    public void Append_AcceptsSampleAfterMinInterval()
    {
        var list = new List<BatteryHistorySample> { Sample(1000, 80) };

        bool added = BatteryHistoryService.Append(list, Sample(1060, 79), capacity: 100, minInterval: TimeSpan.FromMinutes(1));

        Assert.True(added);
        Assert.Equal(2, list.Count);
        Assert.Equal(79, list[^1].Pct);
    }

    [Fact]
    public void Append_FirstSampleAlwaysAccepted()
    {
        var list = new List<BatteryHistorySample>();

        bool added = BatteryHistoryService.Append(list, Sample(0, 50), capacity: 100, minInterval: TimeSpan.FromMinutes(1));

        Assert.True(added);
        Assert.Single(list);
    }

    [Fact]
    public void Append_TrimsOldestBeyondCapacity()
    {
        var list = new List<BatteryHistorySample>();
        // 5 samples, 60s apart, capacity 3 -> only the last 3 remain.
        for (int i = 0; i < 5; i++)
            BatteryHistoryService.Append(list, Sample(i * 60, 100 - i), capacity: 3, minInterval: TimeSpan.FromMinutes(1));

        Assert.Equal(3, list.Count);
        Assert.Equal(120, list[0].T); // oldest two (t=0, t=60) dropped
        Assert.Equal(240, list[^1].T);
    }

    [Fact]
    public void Record_IgnoresUnavailableState()
    {
        var service = new BatteryHistoryService(HistoryPath, minInterval: TimeSpan.Zero);

        Assert.False(service.Record(null, null, DateTime.UtcNow));
        Assert.False(service.Record(new BatteryPowerState { Available = false }, null, DateTime.UtcNow));
        Assert.Empty(service.GetHistory());
        Assert.False(File.Exists(HistoryPath));
    }

    [Fact]
    public void Record_PersistsAvailableSampleAndReloads()
    {
        var state = new BatteryPowerState
        {
            Available = true, OnAc = true, BatteryPercent = 77, PowerWatts = 12.3,
        };

        var service = new BatteryHistoryService(HistoryPath, minInterval: TimeSpan.Zero);
        bool recorded = service.Record(state, temp: 41.5, nowUtc: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).UtcDateTime);

        Assert.True(recorded);
        Assert.True(File.Exists(HistoryPath));

        // A fresh service over the same file must reload the persisted sample.
        var reloaded = new BatteryHistoryService(HistoryPath, minInterval: TimeSpan.Zero).GetHistory();
        Assert.Single(reloaded);
        Assert.Equal(77, reloaded[0].Pct);
        Assert.Equal(12.3, reloaded[0].W);
        Assert.True(reloaded[0].Ac);
        Assert.Equal(41.5, reloaded[0].Temp);
        Assert.Equal(1_700_000_000, reloaded[0].T);
    }

    [Fact]
    public void Record_DropsNonPositiveTemp()
    {
        var state = new BatteryPowerState { Available = true, BatteryPercent = 50 };
        var service = new BatteryHistoryService(HistoryPath, minInterval: TimeSpan.Zero);

        service.Record(state, temp: 0, nowUtc: DateTime.UtcNow);

        Assert.Null(service.GetHistory()[0].Temp);
    }
}
