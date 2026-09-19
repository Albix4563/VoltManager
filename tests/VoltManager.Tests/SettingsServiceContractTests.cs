using System.IO;
using System.Reflection;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class SettingsServiceContractTests
{
    [Fact]
    public void Current_ReturnsDetachedSnapshot()
    {
        var settings = TestSettings.Create(out string path);
        try
        {
            var snapshot = settings.Current;
            snapshot.Language = "it";
            snapshot.PlanGuidMap["PowerSaver"] = "snapshot-only";

            Assert.Equal("", settings.Current.Language);
            Assert.False(settings.Current.PlanGuidMap.ContainsKey("PowerSaver"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Update_DoesNotPublishReplacementWhenPersistenceFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string blockingParent = Path.Combine(root, "not-a-directory");
        File.WriteAllText(blockingParent, "block");
        var settings = new SettingsService(Path.Combine(blockingParent, "settings.json"));

        try
        {
            var replacement = new AppSettings { Language = "it" };

            Assert.ThrowsAny<IOException>(() => settings.Update(replacement));
            Assert.Equal("", settings.Current.Language);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Save_IsolatesFailingSettingsChangedSubscribers()
    {
        var settings = TestSettings.Create(out string path);
        try
        {
            int healthySubscriberCalls = 0;
            settings.SettingsChanged += _ => throw new InvalidOperationException("subscriber failure");
            settings.SettingsChanged += _ => healthySubscriberCalls++;

            settings.Save();

            Assert.Equal(1, healthySubscriberCalls);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void SettingsChanged_GivesEachSubscriberAnIndependentSnapshot()
    {
        var settings = TestSettings.Create(out string path);
        try
        {
            string? observedLanguage = null;
            settings.SettingsChanged += state => state.Language = "mutated-by-subscriber";
            settings.SettingsChanged += state => observedLanguage = state.Language;

            settings.Save();

            Assert.Equal("", observedLanguage);
            Assert.Equal("", settings.Current.Language);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task ControlledUpdates_PreserveConcurrentIndependentChanges()
    {
        MethodInfo? update = typeof(SettingsService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(method =>
            {
                if (method.Name != nameof(SettingsService.Update)) return false;
                ParameterInfo[] parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == typeof(Action<AppSettings>);
            });

        Assert.NotNull(update);

        var settings = TestSettings.Create(out string path);
        try
        {
            const int updateCount = 24;
            using var start = new ManualResetEventSlim(false);
            Task[] tasks = Enumerable.Range(0, updateCount)
                .Select(index => Task.Run(() =>
                {
                    start.Wait();
                    Action<AppSettings> mutation = state =>
                        state.PlanGuidMap[$"plan-{index}"] = $"guid-{index}";
                    update!.Invoke(settings, [mutation]);
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(tasks);

            var snapshot = settings.Current;
            Assert.Equal(updateCount, snapshot.PlanGuidMap.Count);
            for (int index = 0; index < updateCount; index++)
                Assert.Equal($"guid-{index}", snapshot.PlanGuidMap[$"plan-{index}"]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void LegacyFixture_MigratesWithoutLosingUserPreferences()
    {
        string path = CopyFixtureToIsolatedSettings("legacy-settings.json");
        string root = Path.GetDirectoryName(path)!;
        try
        {
            var loaded = new SettingsService(path).Current;

            Assert.False(loaded.CloseToTray);
            Assert.True(loaded.StartWithWindows);
            Assert.False(loaded.MasterAutomationEnabled);
            Assert.Equal("it", loaded.Language);
            Assert.Equal("arial", loaded.Font);
            Assert.Equal("Albix4563/power_efficency", loaded.UpdateRepo);
            Assert.Equal(50, loaded.PowerSourcePlan.LowBatteryThresholdPercent);
            Assert.Equal("stable", loaded.AutoUpdates.UpdateChannel);
            Assert.Equal(30, loaded.AutoUpdates.IntervalMinutes);
            Assert.Equal(20, loaded.Rules.Single(rule => rule.Id == "saver").ThresholdPct);

            string migratedJson = File.ReadAllText(path);
            Assert.DoesNotContain("\"theme\":", migratedJson, StringComparison.Ordinal);
            Assert.Contains("\"themeColor\"", migratedJson, StringComparison.Ordinal);
            Assert.Contains("\"closeToTray\": false", migratedJson, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptFixture_IsBackedUpAndDefaultsAreRecovered()
    {
        string path = CopyFixtureToIsolatedSettings("corrupt-settings.json");
        string root = Path.GetDirectoryName(path)!;
        try
        {
            string original = File.ReadAllText(path);

            var loaded = new SettingsService(path).Current;

            Assert.Equal(AppThemeColor.Blue, loaded.ThemeColor);
            Assert.True(loaded.CloseToTray);
            Assert.Equal("", loaded.Language);
            Assert.True(File.Exists(path + ".corrupt"));
            Assert.Equal(original, File.ReadAllText(path + ".corrupt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CopyFixtureToIsolatedSettings(string fixtureName)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", fixtureName);
        Assert.True(File.Exists(source), $"Fixture missing: {source}");

        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string destination = Path.Combine(root, "settings.json");
        File.Copy(source, destination);
        return destination;
    }
}
