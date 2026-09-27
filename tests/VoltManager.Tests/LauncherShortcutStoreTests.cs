using System.IO;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class LauncherShortcutStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"voltmanager-shortcuts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void IsTransientPath_recognizes_temp_and_chrome_drag_paths()
    {
        string tempRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        Assert.True(LauncherShortcutStore.IsTransientPath(Path.Combine(tempRoot, "drop", "App.lnk")));
        Assert.True(LauncherShortcutStore.IsTransientPath(@"C:\ProgramData\chrome_drag123_4\App.lnk"));
        Assert.False(LauncherShortcutStore.IsTransientPath(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\App.lnk"));
        Assert.False(LauncherShortcutStore.IsTransientPath(tempRoot + "-other" + Path.DirectorySeparatorChar + "App.lnk"));
    }

    [Fact]
    public void PersistDropped_copies_transient_shortcut_to_stable_content_path()
    {
        string sourceDirectory = Path.Combine(_root, "chrome_drag1_2");
        string source = Path.Combine(sourceDirectory, "Epic Games Launcher.lnk");
        string storeRoot = Path.Combine(_root, "store");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllBytes(source, [1, 2, 3, 4, 5]);
        var store = new LauncherShortcutStore(storeRoot);

        string? first = store.PersistDropped(source);
        string? second = store.PersistDropped(source);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal("Epic Games Launcher.lnk", Path.GetFileName(first));
        Assert.True(File.Exists(first));
        Assert.StartsWith(storeRoot, first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PersistDropped_returns_non_transient_path_unchanged()
    {
        const string path = @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\App.lnk";
        var store = new LauncherShortcutStore(Path.Combine(_root, "store"));

        Assert.Equal(path, store.PersistDropped(path));
    }

    [Fact]
    public void PersistDropped_rejects_transient_executable()
    {
        string path = Path.Combine(_root, "chrome_drag1_2", "App.exe");
        var store = new LauncherShortcutStore(Path.Combine(_root, "store"));

        Assert.Null(store.PersistDropped(path));
    }
}
