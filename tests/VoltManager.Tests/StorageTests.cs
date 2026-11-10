using System.IO;
using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "VoltManager.Storage." + Guid.NewGuid().ToString("N"));
    public StorageTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Scan_counts_recursive_bytes_and_orders_root_entries()
    {
        File.WriteAllBytes(Path.Combine(_root, "root.bin"), new byte[10]);
        string nested = Path.Combine(_root, "folder", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "child.bin"), new byte[90]);
        var result = StorageService.ScanDirectory(_root, CancellationToken.None);
        Assert.False(result.Partial);
        Assert.Equal(100, result.ScannedBytes);
        Assert.Equal(100, result.Entries.Sum(entry => entry.Bytes));
        Assert.Equal("folder", result.Entries[0].Name);
        Assert.True(result.Entries[0].IsDirectory);
        Assert.Equal(90, result.Entries[0].Bytes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.Equal(100, json.RootElement.GetProperty("scannedBytes").GetInt64());
        Assert.Equal("folder", json.RootElement.GetProperty("entries")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Limits_produce_partial_bounded_results_including_directory_only_trees()
    {
        for (int index = 0; index < 10; index++) Directory.CreateDirectory(Path.Combine(_root, $"dir{index}"));
        var limited = StorageService.ScanDirectory(_root, CancellationToken.None, maxEntries: 3);
        Assert.True(limited.Partial);
        Assert.True(limited.Entries.Count <= 3);
        var rootLimited = StorageService.ScanDirectory(_root, CancellationToken.None, maxRootEntries: 2);
        Assert.True(rootLimited.Partial);
        Assert.Equal(2, rootLimited.Entries.Count);
        Assert.True(rootLimited.SkippedCount > 0);
        var timed = StorageService.ScanDirectory(_root, CancellationToken.None, TimeSpan.Zero);
        Assert.True(timed.Partial);
        Assert.Empty(timed.Entries);
    }

    [Fact]
    public void File_limit_never_inflates_accounted_bytes()
    {
        for (int index = 0; index < 5; index++) File.WriteAllBytes(Path.Combine(_root, $"{index}.bin"), new byte[10]);
        var result = StorageService.ScanDirectory(_root, CancellationToken.None, maxEntries: 2);
        Assert.True(result.Partial);
        Assert.Equal(20, result.ScannedBytes);
        Assert.Equal(result.ScannedBytes, result.Entries.Sum(entry => entry.Bytes));
    }

    [Fact]
    public void Cancelled_scan_does_not_touch_missing_directory()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => StorageService.ScanDirectory(
            Path.Combine(_root, "missing"), source.Token));
    }

    [Fact]
    public void Missing_directory_is_reported_as_partial()
    {
        var result = StorageService.ScanDirectory(Path.Combine(_root, "missing"), CancellationToken.None);
        Assert.True(result.Partial);
        Assert.Equal(1, result.SkippedCount);
        Assert.Empty(result.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:")]
    [InlineData("C:\\Windows")]
    [InlineData("C:\\..")]
    [InlineData("relative")]
    [InlineData("\\\\server\\share")]
    [InlineData("//server/share")]
    [InlineData("1:\\")]
    public void Invalid_roots_are_rejected_without_enumerating_drives(string root)
    {
        var storage = new StorageService(() => throw new InvalidOperationException("Non deve enumerare."));
        Assert.Throws<ArgumentException>(() => storage.ValidateRoot(root));
    }

    [Fact]
    public async Task Handler_dispatches_drive_listing_and_validates_payload()
    {
        var handler = new StorageRpcHandler(new StorageService(() => []));
        var result = await handler.HandleAsync("getStorageDrives", default, CancellationToken.None);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<StorageDriveInfo>>(result));
        using var payload = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync("getStorageAnalysis", payload.RootElement, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync("unknown", default, CancellationToken.None));
    }

    [Fact]
    public void Junctions_are_not_followed()
    {
        string target = Path.Combine(_root, "target");
        string link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "data.bin"), new byte[50]);
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!)
        {
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var result = StorageService.ScanDirectory(_root, CancellationToken.None);
            Assert.True(result.Partial);
            Assert.Equal(1, result.SkippedCount);
            Assert.Equal(50, result.ScannedBytes);
            Assert.DoesNotContain(result.Entries, entry => entry.Name == "link");
            var linkedRoot = StorageService.ScanDirectory(link, CancellationToken.None);
            Assert.True(linkedRoot.Partial);
            Assert.Empty(linkedRoot.Entries);
        }
        finally { Directory.Delete(link); }
    }
}
