using System.IO;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class UpdateRepoValidationTests
{
    [Fact]
    public void Legacy_repo_is_migrated_to_current_repo()
    {
        using var settings = new TempSettings("{\"updateRepo\":\"Albix4563/power_efficency\"}");

        AppSettings loaded = new SettingsService(settings.Path).Current;

        Assert.Equal(AppSettings.DefaultUpdateRepo, loaded.UpdateRepo);
    }

    [Fact]
    public void Unapproved_repo_is_replaced_by_default_on_load()
    {
        using var settings = new TempSettings("{\"updateRepo\":\"attacker/VoltManager\"}");

        AppSettings loaded = new SettingsService(settings.Path).Current;

        Assert.Equal(AppSettings.DefaultUpdateRepo, loaded.UpdateRepo);
    }

    [Fact]
    public void Allowed_repo_comparison_is_case_insensitive_and_canonicalized()
    {
        using var settings = new TempSettings("{\"updateRepo\":\"aLbIx4563/vOlTmAnAgEr\"}");

        AppSettings loaded = new SettingsService(settings.Path).Current;

        Assert.Equal(AppSettings.DefaultUpdateRepo, loaded.UpdateRepo);
    }

    private sealed class TempSettings : IDisposable
    {
        private readonly string _root;

        internal TempSettings(string json)
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "settings.json");
            File.WriteAllText(Path, json);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
