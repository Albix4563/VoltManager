using System.Text.Json;
using VoltManager.Bridge;
using VoltManager.Bridge.Handlers;

namespace VoltManager.Tests;

public class WidgetRpcHandlerTests
{
    private static JsonElement Payload(object value)
        => JsonSerializer.SerializeToElement(value, BridgeRpc.JsonOpts);

    [Fact]
    public void Methods_match_widget_contract()
    {
        var handler = Create();
        string[] expected =
        [
            "beginWidgetDrag", "setWidgetTopmost", "closeWidget",
            "getWidgetsState", "setWidgetEnabled", "setWidgetsMaster",
            "setWidgetPinned", "setWidgetSize", "setWidgetPlacement",
            "resetWidgetPosition",
        ];
        Assert.Equal(expected.OrderBy(x => x), handler.Methods.OrderBy(x => x));
    }

    [Fact]
    public async Task SetWidgetPlacement_passes_validated_values_and_returns_snapshot()
    {
        (string type, string monitor, string anchor)? call = null;
        var handler = Create(setPlacement: (type, monitor, anchor) =>
        {
            call = (type, monitor, anchor);
            return new { type, monitor, anchor };
        });

        object? result = await handler.HandleAsync("setWidgetPlacement",
            Payload(new { type = "power", monitorId = "display-2", anchor = "bottom-right" }),
            CancellationToken.None);

        Assert.Equal(("power", "display-2", "bottom-right"), call);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.Equal("power", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SetWidgetTopmost_invokes_native_callback_and_preserves_result_shape()
    {
        bool? requested = null;
        var handler = Create(setTopmost: value => requested = value);

        object? result = await handler.HandleAsync("setWidgetTopmost",
            Payload(new { topmost = true }), CancellationToken.None);

        Assert.True(requested);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("topmost").GetBoolean());
    }

    [Fact]
    public async Task Invalid_payload_does_not_invoke_widget_mutation()
    {
        int calls = 0;
        var handler = Create(setEnabled: (_, _) =>
        {
            calls++;
            return new { };
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.HandleAsync("setWidgetEnabled", Payload(new { type = "power" }), CancellationToken.None));
        Assert.Equal(0, calls);
    }

    private static WidgetRpcHandler Create(
        Func<string, bool, object>? setEnabled = null,
        Func<string, string, string, object>? setPlacement = null,
        Action<bool>? setTopmost = null)
    {
        return new WidgetRpcHandler(new WidgetRpcActions(
            GetState: () => new { enabled = true },
            SetEnabled: setEnabled ?? ((type, enabled) => new { type, enabled }),
            SetMasterEnabled: enabled => new { enabled },
            SetPinned: (type, pinned) => new { type, pinned },
            SetSize: (type, size) => new { type, size },
            SetPlacement: setPlacement ?? ((type, monitor, anchor) => new { type, monitor, anchor }),
            ResetPosition: type => new { type },
            BeginDrag: () => { },
            SetTopmost: setTopmost ?? (_ => { }),
            Close: () => { }));
    }
}
