using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Bridge.Rpc;

namespace VoltManager.Tests;

public class MonitoringRpcHandlerTests
{
    private sealed class FakeDialogs : IBridgeFileDialogService
    {
        public string? SavePath { get; set; }
        public Task<string?> SaveFileAsync(BridgeSaveFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult(SavePath);
        public Task<string?> OpenFileAsync(BridgeOpenFileRequest request, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);
    }

    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    [Fact]
    public void Methods_match_monitoring_contract()
    {
        var handler = Create();
        string[] expected =
        [
            "getSystemInfo", "getHeavyAppStatus", "refreshHeavyAppDetection",
            "getAppPowerProfileStatus", "pickAppPowerProfileExecutable",
            "getTopProcesses", "getMemoryStatus", "purgeStandbyList",
            "openLogFolder", "exportDiagnostics",
        ];
        Assert.Equal(expected.OrderBy(x => x), handler.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task GetTopProcesses_defaults_to_eight()
    {
        int received = 0;
        var handler = Create(getTopProcesses: count =>
        {
            received = count;
            return new[] { count };
        });

        await handler.HandleAsync("getTopProcesses", default, CancellationToken.None);
        Assert.Equal(8, received);
    }

    [Fact]
    public async Task PurgeStandbyList_returns_purge_result_and_fresh_memory()
    {
        var handler = Create(purge: () => true, memory: () => new { freeGb = 9 });
        object? result = await handler.HandleAsync("purgeStandbyList", default, CancellationToken.None);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(9, doc.RootElement.GetProperty("memory").GetProperty("freeGb").GetInt32());
    }

    [Fact]
    public async Task ExportDiagnostics_cancelled_does_not_build_report()
    {
        int builds = 0;
        var handler = Create(buildDiagnostics: () =>
        {
            builds++;
            return "report";
        }, dialogs: new FakeDialogs { SavePath = null });

        object? result = await handler.HandleAsync("exportDiagnostics", default, CancellationToken.None);

        Assert.Equal(0, builds);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
    }

    private static MonitoringRpcHandler Create(
        Func<int, object>? getTopProcesses = null,
        Func<bool>? purge = null,
        Func<object>? memory = null,
        Func<string>? buildDiagnostics = null,
        FakeDialogs? dialogs = null)
        => new(
            new MonitoringRpcActions(
                GetSystemInfo: () => new { os = "Windows" },
                GetHeavyAppStatus: () => new { active = false },
                RefreshHeavyAppDetection: () => new { active = false },
                GetAppPowerProfileStatus: () => new { active = false },
                PickAppPowerProfileExecutable: _ => Task.FromResult<string?>(null),
                GetTopProcesses: getTopProcesses ?? (count => new[] { count }),
                GetMemoryStatus: memory ?? (() => new { freeGb = 4 }),
                PurgeStandbyList: purge ?? (() => true),
                OpenLogFolder: () => (true, "C:\\logs", (string?)null),
                BuildDiagnosticsReport: buildDiagnostics ?? (() => "report"),
                LocalNow: () => new DateTime(2026, 9, 20, 12, 0, 0)),
            dialogs ?? new FakeDialogs());
}
