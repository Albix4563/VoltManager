using System.IO;
using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanProfileStoreTests
{
    [Fact]
    public void Save_and_get_roundtrip_versioned_profile_data()
    {
        string directory = TempDirectory();
        try
        {
            var store = new FanProfileStore(directory);
            var profile = ValidProfile();

            var summary = store.Save(profile);
            var loaded = store.Get(summary.Id);

            Assert.Equal(FanProfileValidator.Format, loaded.Format);
            Assert.Equal(FanProfileValidator.SchemaVersion, loaded.SchemaVersion);
            Assert.Equal("Test profile", loaded.Name);
            Assert.Single(loaded.Fans);
            Assert.Single(store.List());
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [Fact]
    public void Import_rejects_unknown_payload_members_in_schema_version_1()
    {
        string directory = TempDirectory();
        string source = Path.Combine(directory, "untrusted.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(source, """
            {
              "format": "voltmanager.fan-profile",
              "schemaVersion": 1,
              "id": "external",
              "name": "External",
              "fans": [],
              "groups": [],
              "uiPreferences": {},
              "unexpectedField": "not part of schema"
            }
            """);

            var store = new FanProfileStore(Path.Combine(directory, "profiles"));
            var topology = new FanTopology { Revision = "rev", SensorsAvailable = true };

            Assert.ThrowsAny<InvalidDataException>(() => store.Import(source, topology));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    private static FanProfile ValidProfile() => new()
    {
        Id = "test-profile",
        Name = "Test profile",
        Fans = new List<FanProfileFan>
        {
            new()
            {
                ProfileFanId = "cpu",
                DisplayName = "CPU Fan",
                MatchHints = new FanMatchHints { Role = FanRole.CpuFan },
            }
        }
    };

    private static string TempDirectory() => Path.Combine(Path.GetTempPath(), "VoltManager-FanTests-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch { }
    }
}
