using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class UpdateRpcHandlerTests
{
    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    [Fact]
    public void Methods_match_update_contract()
    {
        var handler = Create();
        Assert.Equal(
            new[] { "checkForUpdates", "downloadUpdate", "getReleaseHistory" },
            handler.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task DownloadUpdate_defers_before_network_when_heavy_app_active()
    {
        int downloads = 0, defers = 0;
        var handler = Create(
            heavy: () => true,
            download: (_, _) => { downloads++; return Task.FromResult(UpdateTestHelpers.CreateVerifiedDownload()); },
            defer: _ => defers++);

        object? result = await handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None);

        Assert.Equal(0, downloads);
        Assert.Equal(1, defers);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("deferred").GetBoolean());
    }

    [Fact]
    public async Task DownloadUpdate_rejects_url_not_approved_by_update_service()
    {
        int downloads = 0;
        var handler = Create(
            allowed: _ => false,
            download: (_, _) => { downloads++; return Task.FromResult(UpdateTestHelpers.CreateVerifiedDownload()); });

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://evil.example/update.exe" }), CancellationToken.None));

        Assert.Equal(0, downloads);
    }

    [Fact]
    public async Task DownloadUpdate_passes_rpc_cancellation_token_to_download()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;
        var handler = Create(download: (_, token) =>
        {
            observed = token;
            return Task.FromResult(UpdateTestHelpers.CreateVerifiedDownload());
        });

        await handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), cts.Token);

        Assert.Equal(cts.Token, observed);
    }

    [Fact]
    public async Task DownloadUpdate_launches_downloaded_installer_and_requests_exit()
    {
        string? launchedPath = null, launchedArgs = null;
        int exits = 0;
        var handler = Create(
            launch: (download, args, _) =>
            {
                launchedPath = download.Path;
                launchedArgs = args;
                return Task.CompletedTask;
            },
            exit: () => exits++);

        object? result = await handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None);

        Assert.EndsWith(".exe", launchedPath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/update --pid ", launchedArgs);
        Assert.Contains("--lang it", launchedArgs);
        Assert.Equal(1, exits);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task DownloadUpdate_waits_for_setup_readiness_before_exit()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int exits = 0;
        var handler = Create(
            launch: (_, _, _) => ready.Task,
            exit: () => exits++);

        Task<object?> update = handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None);

        Assert.False(update.IsCompleted);
        Assert.Equal(0, exits);
        ready.SetResult();
        await update;
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task DownloadUpdate_failed_or_cancelled_setup_does_not_exit()
    {
        int exits = 0;
        var handler = Create(
            launch: (_, _, _) => Task.FromException(new TimeoutException("Setup not ready")),
            exit: () => exits++);

        await Assert.ThrowsAsync<TimeoutException>(() => handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None));
        Assert.Equal(0, exits);
    }

    [Fact]
    public async Task DownloadUpdate_committed_handoff_exits_even_if_rpc_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        int exits = 0;
        var handler = Create(
            launch: (_, _, _) =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            },
            exit: () => exits++);

        await handler.HandleAsync("downloadUpdate",
            Payload(new { url = "https://example/update.exe" }), cts.Token);
        Assert.Equal(1, exits);
    }

    [Fact]
    public async Task DownloadUpdate_rejects_parallel_install_attempt()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int downloads = 0;
        var handler = Create(
            download: (_, _) =>
            {
                downloads++;
                return Task.FromResult(UpdateTestHelpers.CreateVerifiedDownload());
            },
            launch: (_, _, _) => ready.Task);

        Task<object?> first = handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None));
        Assert.Equal(1, downloads);

        ready.SetResult();
        await first;
    }

    private static UpdateRpcHandler Create(
        Func<bool>? heavy = null,
        Func<string, bool>? allowed = null,
        Func<string, CancellationToken, Task<VerifiedUpdateDownload>>? download = null,
        Action<string>? defer = null,
        Func<VerifiedUpdateDownload, string, CancellationToken, Task>? launch = null,
        Action? exit = null)
        => new(new LocalizationService(), new UpdateRpcActions(
            CheckForUpdates: () => Task.FromResult(new UpdateInfo()),
            GetReleaseHistory: () => Task.FromResult(new ReleaseHistory()),
            IsDownloadUrlAllowed: allowed ?? (_ => true),
            DownloadUpdate: download ?? ((_, _) => Task.FromResult(UpdateTestHelpers.CreateVerifiedDownload())),
            IsHeavyAppSessionActive: heavy ?? (() => false),
            DeferUpdateUntilGameEnds: defer ?? (_ => { }),
            LaunchInstaller: launch ?? ((_, _, _) => Task.CompletedTask),
            RequestExit: exit ?? (() => { })));
}
