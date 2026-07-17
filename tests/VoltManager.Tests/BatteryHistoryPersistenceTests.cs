using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class BatteryHistoryPersistenceTests
{
    [Fact]
    public void Record_ReplacesHistoryWithoutLeavingTemporaryFile_AndReloadsState()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "battery-history.json");
        Directory.CreateDirectory(directory);

        try
        {
            var service = new BatteryHistoryService(path, capacity: 32, minInterval: TimeSpan.FromSeconds(1));
            var state = new BatteryPowerState
            {
                Available = true,
                OnAc = false,
                Status = "discharging",
                PowerWatts = -12.5,
                BatteryPercent = 73,
            };

            bool recorded = service.Record(state, temp: 44.2, new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc));

            Assert.True(recorded);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            var reloaded = new BatteryHistoryService(path, capacity: 32, minInterval: TimeSpan.FromSeconds(1));
            BatteryHistorySample sample = Assert.Single(reloaded.GetHistory());
            Assert.Equal(73, sample.Pct);
            Assert.Equal(-12.5, sample.W);
            Assert.False(sample.Ac);
            Assert.Equal(44.2, sample.Temp);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
