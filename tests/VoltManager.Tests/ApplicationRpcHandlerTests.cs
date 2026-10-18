using System.IO;
using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;
using VoltManager.Localization;

namespace VoltManager.Tests;

public class ApplicationRpcHandlerTests
{
    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    [Fact]
    public void Methods_match_application_contract()
    {
        var handler = Create();
        string[] expected =
        [
            "getGamingMode", "setGamingMode", "getStartupApps", "pickStartupExecutable",
            "addStartupApp", "setStartupAppEnabled", "removeStartupApp", "logError",
            "openExternal", "exitApp", "minimizeToTray", "showMainWindow",
        ];
        Assert.Equal(expected.OrderBy(x => x), handler.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task OpenExternal_launches_only_http_urls()
    {
        var launched = new List<string>();
        var handler = Create(openExternal: launched.Add);

        await handler.HandleAsync("openExternal", Payload(new { url = "file:///tmp/x" }), CancellationToken.None);
        await handler.HandleAsync("openExternal", Payload(new { url = "https://example.test" }), CancellationToken.None);

        Assert.Equal(new[] { "https://example.test" }, launched);
    }

    [Fact]
    public async Task Exit_minimize_and_show_use_callbacks()
    {
        int exits = 0, minimizes = 0, shows = 0;
        var handler = Create(exit: () => exits++, minimize: () => minimizes++, show: () => shows++);
        await handler.HandleAsync("exitApp", default, CancellationToken.None);
        await handler.HandleAsync("minimizeToTray", default, CancellationToken.None);
        await handler.HandleAsync("showMainWindow", default, CancellationToken.None);
        Assert.Equal(1, exits);
        Assert.Equal(1, minimizes);
        Assert.Equal(1, shows);
    }

    [Fact]
    public async Task AddStartupApp_requires_path_before_callback()
    {
        int calls = 0;
        var handler = Create(addStartup: _ => { calls++; return new { }; });
        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync("addStartupApp", Payload(new { }), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AddStartupApp_accepts_only_the_most_recent_picked_path_once()
    {
        const string picked = @"C:\Tools\Picked.exe";
        int calls = 0;
        string? added = null;
        var handler = Create(
            pickStartup: _ => Task.FromResult<string?>(picked),
            addStartup: path => { calls++; added = path; return new { path }; });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync("addStartupApp", Payload(new { path = @"C:\Tools\Other.exe" }), CancellationToken.None));
        Assert.Equal(0, calls);

        await handler.HandleAsync("pickStartupExecutable", default, CancellationToken.None);
        await handler.HandleAsync("addStartupApp", Payload(new { path = @"c:\tools\PICKED.exe" }), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(Path.GetFullPath(picked), added, ignoreCase: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync("addStartupApp", Payload(new { path = picked }), CancellationToken.None));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Canceled_startup_file_picker_invalidates_a_previous_selection()
    {
        const string stale = @"C:\Tools\PreviouslySelected.exe";
        int picks = 0;
        int addCalls = 0;
        var handler = Create(
            pickStartup: _ => Task.FromResult<string?>(++picks == 1 ? stale : null),
            addStartup: _ => { addCalls++; return new { success = true }; });

        object? first = await handler.HandleAsync("pickStartupExecutable", default, CancellationToken.None);
        Assert.Equal(Path.GetFullPath(stale),
            first?.GetType().GetProperty("path")?.GetValue(first));

        object? canceled = await handler.HandleAsync("pickStartupExecutable", default, CancellationToken.None);
        Assert.Null(canceled?.GetType().GetProperty("path")?.GetValue(canceled));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync("addStartupApp", Payload(new { path = stale }), CancellationToken.None));
        Assert.Equal(0, addCalls);
    }

    private static ApplicationRpcHandler Create(
        Action<string>? openExternal = null,
        Action? exit = null,
        Action? minimize = null,
        Func<string, object>? addStartup = null,
        Func<CancellationToken, Task<string?>>? pickStartup = null,
        Action? show = null)
        => new(new LocalizationService(), new ApplicationRpcActions(
            GetGamingMode: () => new { active = false },
            SetGamingMode: _ => Task.FromResult<object?>(new { active = true }),
            GetStartupApps: () => new { enabled = Array.Empty<object>(), disabled = Array.Empty<object>() },
            PickStartupExecutable: pickStartup ?? (_ => Task.FromResult<string?>(null)),
            AddStartupApp: addStartup ?? (path => new { path }),
            SetStartupAppEnabled: (_, _) => true,
            RemoveStartupApp: _ => true,
            LogError: _ => { },
            OpenExternal: openExternal ?? (_ => { }),
            RequestExit: exit ?? (() => { }),
            RequestMinimize: minimize ?? (() => { }),
            ShowMainWindow: show ?? (() => { })));
}
