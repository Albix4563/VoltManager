using System.IO;
using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanControlRecoveryStoreTests
{
    [Fact]
    public void Recovery_lease_round_trips_and_empty_save_removes_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "fan-recovery.json");
        try
        {
            var store = new FanControlRecoveryStore(path);
            store.Save(new[]
            {
                new FanControlRecoveryEntry
                {
                    FanId = "fan-1",
                    ControlIdentifier = "/control/1",
                    Backend = "fake",
                    DisplayName = "CPU Fan",
                }
            });

            FanControlRecoveryEntry entry = Assert.Single(store.Load());
            Assert.Equal("/control/1", entry.ControlIdentifier);

            store.Save(Array.Empty<FanControlRecoveryEntry>());
            Assert.Empty(store.Load());
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
