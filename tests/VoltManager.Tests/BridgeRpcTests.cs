using System.IO;
using System.Text.Json;
using VoltManager.Bridge;

namespace VoltManager.Tests;

/// <summary>
/// Drives the shipped <see cref="BridgeRpc"/> reply/dispatch-failure path used by
/// <c>HostBridge.HandleMessageAsync</c>. A throwing method must produce ok:false
/// with an error message without implying process termination.
/// </summary>
public class BridgeRpcTests
{
    [Fact]
    public void OnDispatchException_with_id_produces_non_ok_reply_with_error_text()
    {
        var ex = new InvalidOperationException("plan switch denied");
        var failure = BridgeRpc.OnDispatchException("rpc-42", ex);

        Assert.True(failure.ShouldReply);
        Assert.Equal("rpc-42", failure.Id);
        Assert.Equal("plan switch denied", failure.ErrorMessage);
        Assert.Contains("rpc-42", failure.LogMessage);

        string json = BridgeRpc.FormatFailure(failure.Id!, failure.ErrorMessage);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("rpc-42", root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("plan switch denied", root.GetProperty("error").GetString());
        Assert.False(root.TryGetProperty("result", out _));
    }

    [Fact]
    public void OnDispatchException_without_id_skips_reply_but_keeps_log_context()
    {
        var failure = BridgeRpc.OnDispatchException(null, new Exception("malformed"));

        Assert.False(failure.ShouldReply);
        Assert.Null(failure.Id);
        Assert.Equal("malformed", failure.ErrorMessage);
        Assert.Contains("none", failure.LogMessage);
    }

    [Fact]
    public void FormatSuccess_uses_same_shape_as_host_replies()
    {
        string json = BridgeRpc.FormatSuccess("rpc-1", new { success = true, value = 3 });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.Equal(3, root.GetProperty("result").GetProperty("value").GetInt32());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public void FormatFailure_from_exception_matches_dispatch_path()
    {
        var ex = new ArgumentException("bad payload");
        string viaHelper = BridgeRpc.FormatFailure("rpc-9", ex);
        var failure = BridgeRpc.OnDispatchException("rpc-9", ex);
        string viaFailure = BridgeRpc.FormatFailure(failure.Id!, failure.ErrorMessage);

        Assert.Equal(viaFailure, viaHelper);
        using var doc = JsonDocument.Parse(viaHelper);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("bad payload", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void HandleLogError_invokes_logger_and_never_throws()
    {
        string? logged = null;
        object result = BridgeRpc.HandleLogError("boom", "at line 1", msg => logged = msg);

        Assert.NotNull(logged);
        Assert.Contains("[UI] boom", logged);
        Assert.Contains("at line 1", logged);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void HandleLogError_swallows_logger_failures()
    {
        object result = BridgeRpc.HandleLogError("x", null, _ => throw new IOException("disk full"));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result, BridgeRpc.JsonOpts));
        Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Simulated_throwing_dispatch_yields_non_ok_payload_like_HandleMessageAsync()
    {
        // Mirrors HostBridge.HandleMessageAsync catch path without WebView2.
        string? id = "rpc-sim";
        Exception thrown = new Exception("method blew up");

        BridgeRpc.DispatchFailure failure = BridgeRpc.OnDispatchException(id, thrown);
        // Caller logs failure.LogMessage + exception; process continues.
        Assert.Contains("Bridge message handling failed", failure.LogMessage);

        Assert.True(failure.ShouldReply);
        string reply = BridgeRpc.FormatFailure(failure.Id!, failure.ErrorMessage);

        using var doc = JsonDocument.Parse(reply);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("method blew up", doc.RootElement.GetProperty("error").GetString());
    }
}
