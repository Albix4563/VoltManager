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

    private static ApplicationRpcHandler Create(
        Action<string>? openExternal = null,
        Action? exit = null,
        Action? minimize = null,
        Func<string, object>? addStartup = null,
        Action? show = null)
        => new(new LocalizationService(), new ApplicationRpcActions(
            GetGamingMode: () => new { active = false },
            SetGamingMode: _ => Task.FromResult<object?>(new { active = true }),
            GetStartupApps: () => new { enabled = Array.Empty<object>(), disabled = Array.Empty<object>() },
            PickStartupExecutable: _ => Task.FromResult<string?>(null),
            AddStartupApp: addStartup ?? (path => new { path }),
            SetStartupAppEnabled: (_, _) => true,
            RemoveStartupApp: _ => true,
            LogError: _ => { },
            OpenExternal: openExternal ?? (_ => { }),
            RequestExit: exit ?? (() => { }),
            RequestMinimize: minimize ?? (() => { }),
            ShowMainWindow: show ?? (() => { })));
}
