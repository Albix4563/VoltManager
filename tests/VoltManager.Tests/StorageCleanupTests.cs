using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using VoltManager.Bridge.Handlers;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class StorageCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoltManager.Cleanup." + Guid.NewGuid().ToString("N"));
    public StorageCleanupTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string OldFile(string name, int size = 10)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }

    [Fact]
    public void Cleanup_preserves_roots_recent_locked_readonly_and_hardlinked_files()
    {
        string old = OldFile("old");
        string recent = OldFile("recent");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow);
        string locked = OldFile("locked");
        string readOnly = OldFile("readonly");
        string target = OldFile("target");
        string link = Path.Combine(_root, "hardlink");
        Assert.True(CreateHardLink(link, target, IntPtr.Zero));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            using var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            var result = StorageTempCleaner.Clean([_root], CancellationToken.None);
            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(10, result.FreedBytes);
            Assert.True(result.Partial);
            Assert.False(File.Exists(old));
            Assert.True(Directory.Exists(_root));
            foreach (var path in new[] { recent, locked, readOnly, target, link }) Assert.True(File.Exists(path));
        }
        finally { File.SetAttributes(readOnly, FileAttributes.Normal); }
    }

    [Fact]
    public void Cleanup_preserves_directories_and_removes_nested_old_files()
    {
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        string path = OldFile("nested/old", 20);
        var result = StorageTempCleaner.Clean([_root, _root], CancellationToken.None);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(20, result.FreedBytes);
        Assert.False(File.Exists(path));
        Assert.True(Directory.Exists(Path.Combine(_root, "nested")));
        Assert.False(result.Partial);
    }

    [Fact]
    public void Limits_and_cancellation_preserve_files_and_report_partial()
    {
        string file = OldFile("old");
        foreach (var result in new[]
        {
            StorageTempCleaner.Clean([_root], new CancellationToken(true)),
            StorageTempCleaner.Clean([_root], CancellationToken.None, TimeSpan.Zero),
            StorageTempCleaner.Clean([_root], CancellationToken.None, maxEntries: 0),
            StorageTempCleaner.Clean([Path.GetPathRoot(_root)!], CancellationToken.None),
            StorageTempCleaner.Clean(["relative"], CancellationToken.None),
        })
        {
            Assert.True(result.Partial);
            Assert.Equal(0, result.DeletedFiles);
        }
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Junctions_in_root_or_ancestors_are_not_followed()
    {
        string target = Path.Combine(_root, "target");
        string junction = Path.Combine(_root, "link");
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        string file = OldFile("target/nested/old");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            foreach (var root in new[] { junction, Path.Combine(junction, "nested") })
            {
                var result = StorageTempCleaner.Clean([root], CancellationToken.None);
                Assert.True(result.Partial);
                Assert.Equal(0, result.DeletedFiles);
                Assert.True(File.Exists(file));
            }
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void Nested_junction_cannot_delete_external_files_and_recent_creation_is_preserved()
    {
        string scope = Path.Combine(_root, "scope");
        string outside = Path.Combine(_root, "outside");
        string junction = Path.Combine(scope, "link");
        Directory.CreateDirectory(scope);
        Directory.CreateDirectory(outside);
        string external = OldFile("outside/old");
        string recent = OldFile("scope/recent");
        File.SetCreationTimeUtc(recent, DateTime.UtcNow);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{junction}\" \"{outside}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var result = StorageTempCleaner.Clean([scope], CancellationToken.None);
            Assert.Equal(0, result.DeletedFiles);
            Assert.True(result.Partial);
            Assert.True(File.Exists(external));
            Assert.True(File.Exists(recent));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void Entry_limit_reports_already_deleted_files()
    {
        OldFile("one");
        OldFile("two");
        var result = StorageTempCleaner.Clean([_root], CancellationToken.None, maxEntries: 1);
        Assert.True(result.Partial);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(10, result.FreedBytes);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Handler_requires_confirmation_and_rejects_concurrent_requests()
    {
        int cleanups = 0;
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StorageRpcHandler(new StorageService(() => []), _ => confirmation.Task, _ =>
        {
            cleanups++;
            return Task.FromResult(new StorageCleanupResult(true, 1, 10, 0, false));
        });
        var first = handler.HandleAsync("cleanStorageTemp", default, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync("cleanStorageTemp", default, CancellationToken.None));
        confirmation.SetResult(false);
        var cancelled = Assert.IsType<StorageCleanupResult>(await first);
        Assert.False(cancelled.Confirmed);
        Assert.Equal(0, cleanups);
        handler = new StorageRpcHandler(new StorageService(() => []), _ => Task.FromResult(true), _ =>
        {
            cleanups++;
            return Task.FromResult(new StorageCleanupResult(true, 1, 10, 0, false));
        });
        Assert.True(Assert.IsType<StorageCleanupResult>(await handler.HandleAsync("cleanStorageTemp", default, CancellationToken.None)).Confirmed);
        Assert.Equal(1, cleanups);
        var unconfigured = new StorageRpcHandler(new StorageService(() => []));
        Assert.False(Assert.IsType<StorageCleanupResult>(await unconfigured.HandleAsync("cleanStorageTemp", default, CancellationToken.None)).Confirmed);
    }

    [Fact]
    public async Task Handler_validates_tool_payload_and_releases_guard_after_failure()
    {
        var handler = new StorageRpcHandler(new StorageService(() => []),
            _ => throw new IOException("Conferma non disponibile."));
        for (int attempt = 0; attempt < 2; attempt++)
            await Assert.ThrowsAsync<IOException>(() => handler.HandleAsync("cleanStorageTemp", default, CancellationToken.None));
        using var missing = System.Text.Json.JsonDocument.Parse("{}");
        using var invalid = System.Text.Json.JsonDocument.Parse("{\"tool\":\"cmd\"}");
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync("openStorageTool", missing.RootElement, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync("openStorageTool", invalid.RootElement, CancellationToken.None));
    }

    [Theory]
    [InlineData("cleanmgr")]
    [InlineData("dfrgui")]
    public void Tool_launch_uses_only_absolute_system_path_and_no_arguments(string tool)
    {
        StorageService.OpenTool(tool, info =>
        {
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), tool + ".exe"), info.FileName);
            Assert.True(info.UseShellExecute);
            Assert.Empty(info.Arguments);
            return true;
        });
        Assert.Throws<InvalidOperationException>(() => StorageService.OpenTool(tool, _ => false));
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("defrag")]
    [InlineData("")]
    [InlineData("C:\\Windows\\System32\\cleanmgr.exe")]
    public void Unknown_tools_cannot_launch(string tool) =>
        Assert.Throws<ArgumentException>(() => StorageService.OpenTool(tool, _ => throw new Exception("Non deve avviarsi.")));

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string file, string existing, IntPtr security);
}
