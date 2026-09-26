using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Localization;
using VoltManager.Models;

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
            download: (_, _) => { downloads++; return Task.FromResult("installer.exe"); },
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
            download: (_, _) => { downloads++; return Task.FromResult("installer.exe"); });

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
            return Task.FromResult("installer.exe");
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
            launch: (path, args) => { launchedPath = path; launchedArgs = args; },
            exit: () => exits++);

        object? result = await handler.HandleAsync(
            "downloadUpdate", Payload(new { url = "https://example/update.exe" }), CancellationToken.None);

        Assert.Equal("installer.exe", launchedPath);
        Assert.Contains("/update --pid ", launchedArgs);
        Assert.Contains("--lang it", launchedArgs);
        Assert.Equal(1, exits);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    private static UpdateRpcHandler Create(
        Func<bool>? heavy = null,
        Func<string, bool>? allowed = null,
        Func<string, CancellationToken, Task<string>>? download = null,
        Action<string>? defer = null,
        Action<string, string>? launch = null,
        Action? exit = null)
        => new(new LocalizationService(), new UpdateRpcActions(
            CheckForUpdates: () => Task.FromResult(new UpdateInfo()),
            GetReleaseHistory: () => Task.FromResult(new ReleaseHistory()),
            IsDownloadUrlAllowed: allowed ?? (_ => true),
            DownloadUpdate: download ?? ((_, _) => Task.FromResult("installer.exe")),
            IsHeavyAppSessionActive: heavy ?? (() => false),
            DeferUpdateUntilGameEnds: defer ?? (_ => { }),
            LaunchInstaller: launch ?? ((_, _) => { }),
            RequestExit: exit ?? (() => { })));
}
