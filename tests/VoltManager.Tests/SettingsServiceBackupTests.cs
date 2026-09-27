using System.IO;
using System.Text.Json;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class SettingsServiceBackupTests
{
    [Fact]
    public void Valid_main_loads_and_refreshes_backup()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "it");

        var loaded = new SettingsService(temp.Path);

        Assert.Equal("it", loaded.Current.Language);
        Assert.True(File.Exists(temp.Path + ".bak"));
    }

    [Fact]
    public void Corrupt_main_with_valid_backup_uses_backup_restores_main_and_keeps_corrupt_copy()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "en");
        settings.Update(state => state.Language = "it");
        File.WriteAllText(temp.Path, "{ corrupt");

        var recovered = new SettingsService(temp.Path);

        Assert.Equal("en", recovered.Current.Language);
        Assert.Equal("en", ReadLanguage(temp.Path));
        Assert.True(File.Exists(temp.Path + ".corrupt"));
        Assert.Contains("corrupt", File.ReadAllText(temp.Path + ".corrupt"), StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_main_and_corrupt_backup_use_defaults()
    {
        using var temp = new TempSettings();
        File.WriteAllText(temp.Path, "{ bad-main");
        File.WriteAllText(temp.Path + ".bak", "{ bad-backup");

        var settings = new SettingsService(temp.Path);

        Assert.Equal("", settings.Current.Language);
        Assert.True(File.Exists(temp.Path + ".corrupt"));
    }

    [Fact]
    public void Corrupt_main_without_backup_keeps_existing_default_behavior()
    {
        using var temp = new TempSettings();
        File.WriteAllText(temp.Path, "{ bad-main");

        var settings = new SettingsService(temp.Path);

        Assert.Equal("", settings.Current.Language);
        Assert.True(File.Exists(temp.Path + ".corrupt"));
        Assert.False(File.Exists(temp.Path + ".bak"));
    }

    [Fact]
    public void Missing_main_with_valid_backup_restores_main()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "it");
        File.Delete(temp.Path);

        var recovered = new SettingsService(temp.Path);

        Assert.Equal("it", recovered.Current.Language);
        Assert.True(File.Exists(temp.Path));
        Assert.Equal("it", ReadLanguage(temp.Path));
    }

    [Fact]
    public void First_run_uses_defaults_without_creating_backup()
    {
        using var temp = new TempSettings();

        var settings = new SettingsService(temp.Path);

        Assert.Equal("", settings.Current.Language);
        Assert.False(File.Exists(temp.Path));
        Assert.False(File.Exists(temp.Path + ".bak"));
    }

    [Fact]
    public void Second_save_keeps_previous_valid_main_as_backup()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "en");
        settings.Update(state => state.Language = "it");

        Assert.Equal("it", ReadLanguage(temp.Path));
        Assert.Equal("en", ReadLanguage(temp.Path + ".bak"));
    }

    [Fact]
    public void Save_after_corrupt_load_never_copies_corrupt_main_into_good_backup()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "en");
        settings.Update(state => state.Language = "it");
        File.WriteAllText(temp.Path, "{ corrupt-main");

        var recovered = new SettingsService(temp.Path);
        Assert.Equal("en", recovered.Current.Language);
        recovered.Update(state => state.Language = "it");

        Assert.Equal("it", ReadLanguage(temp.Path));
        Assert.Equal("en", ReadLanguage(temp.Path + ".bak"));
        Assert.DoesNotContain("corrupt-main", File.ReadAllText(temp.Path + ".bak"), StringComparison.Ordinal);
    }

    [Fact]
    public void Stray_tmp_is_ignored()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "it");
        File.WriteAllText(temp.Path + ".tmp", "{ stale-partial-write");

        var loaded = new SettingsService(temp.Path);

        Assert.Equal("it", loaded.Current.Language);
    }

    [Fact]
    public void Backup_uses_same_migration_and_normalization_pipeline_as_main()
    {
        using var temp = new TempSettings();
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Settings", "legacy-settings.json");
        Assert.True(File.Exists(fixture));
        File.Copy(fixture, temp.Path + ".bak");

        var loaded = new SettingsService(temp.Path);

        Assert.Equal("it", loaded.Current.Language);
        Assert.Equal("Albix4563/power_efficency", loaded.Current.UpdateRepo);
        Assert.True(File.Exists(temp.Path));
        Assert.Equal("Albix4563/power_efficency", ReadString(temp.Path, "UpdateRepo"));
    }

    [Fact]
    public void File_replace_failure_uses_copy_move_fallback()
    {
        using var temp = new TempSettings();
        int replaceCalls = 0;
        var settings = new SettingsService(temp.Path, (_, _, _) =>
        {
            replaceCalls++;
            throw new PlatformNotSupportedException("synthetic");
        });
        settings.Update(state => state.Language = "en");

        settings.Update(state => state.Language = "it");

        Assert.Equal(1, replaceCalls);
        Assert.Equal("it", ReadLanguage(temp.Path));
        Assert.Equal("en", ReadLanguage(temp.Path + ".bak"));
    }

    [Fact]
    public async Task Concurrent_saves_do_not_corrupt_main_file()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        const int count = 64;

        await Task.WhenAll(Enumerable.Range(0, count).Select(index => Task.Run(() =>
            settings.Update(state => state.PlanGuidMap[$"plan-{index}"] = $"guid-{index}"))));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(temp.Path));
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        var reloaded = new SettingsService(temp.Path);
        Assert.Equal(count, reloaded.Current.PlanGuidMap.Count);
        using JsonDocument backup = JsonDocument.Parse(File.ReadAllText(temp.Path + ".bak"));
        Assert.Equal(JsonValueKind.Object, backup.RootElement.ValueKind);
    }

    [Fact]
    public void Locked_main_with_valid_backup_uses_backup_without_touching_main()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "en");
        settings.Update(state => state.Language = "it");
        byte[] newerMain = File.ReadAllBytes(temp.Path);

        SettingsService recovered;
        using (new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            recovered = new SettingsService(temp.Path);

        Assert.Equal("en", recovered.Current.Language);
        Assert.Equal(newerMain, File.ReadAllBytes(temp.Path));
        Assert.False(File.Exists(temp.Path + ".corrupt"));
        Assert.Equal("en", ReadLanguage(temp.Path + ".bak"));
    }

    [Fact]
    public void Locked_main_without_backup_uses_defaults_without_quarantine()
    {
        using var temp = new TempSettings();
        File.WriteAllText(temp.Path, "{\"language\":\"it\"}");
        byte[] original = File.ReadAllBytes(temp.Path);

        SettingsService settings;
        using (new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            settings = new SettingsService(temp.Path);

        Assert.Equal("", settings.Current.Language);
        Assert.Equal(original, File.ReadAllBytes(temp.Path));
        Assert.False(File.Exists(temp.Path + ".corrupt"));
        Assert.False(File.Exists(temp.Path + ".bak"));
    }

    [Fact]
    public async Task Briefly_locked_main_is_retried_and_loaded()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "en");
        settings.Update(state => state.Language = "it");

        var lockHeld = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using var stream = new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.None);
            lockHeld.Set();
            Thread.Sleep(120);
        });
        Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));

        var loaded = new SettingsService(temp.Path);
        await holder;

        Assert.Equal("it", loaded.Current.Language);
        Assert.False(File.Exists(temp.Path + ".corrupt"));
    }

    [Fact]
    public void Save_after_locked_main_recovery_writes_main_and_keeps_backup()
    {
        using var temp = new TempSettings();
        var settings = new SettingsService(temp.Path);
        settings.Update(state => state.Language = "en");
        settings.Update(state => state.Language = "it");

        SettingsService recovered;
        using (new FileStream(temp.Path, FileMode.Open, FileAccess.Read, FileShare.None))
            recovered = new SettingsService(temp.Path);
        recovered.Update(state => state.PlanGuidMap["after-lock"] = "guid-after-lock");

        Assert.Contains("guid-after-lock", File.ReadAllText(temp.Path), StringComparison.Ordinal);
        Assert.Equal("en", ReadLanguage(temp.Path + ".bak"));
        Assert.DoesNotContain("guid-after-lock", File.ReadAllText(temp.Path + ".bak"), StringComparison.Ordinal);
        Assert.Equal("guid-after-lock", new SettingsService(temp.Path).Current.PlanGuidMap["after-lock"]);
    }

    private static string ReadLanguage(string path) => ReadString(path, "Language");

    private static string ReadString(string path, string property)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty(property, out JsonElement value))
            Assert.True(document.RootElement.TryGetProperty(char.ToLowerInvariant(property[0]) + property[1..], out value));
        return value.GetString() ?? "";
    }

    private sealed class TempSettings : IDisposable
    {
        private readonly string _root;
        public TempSettings()
        {
            _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "settings.json");
        }

        public string Path { get; }
        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
