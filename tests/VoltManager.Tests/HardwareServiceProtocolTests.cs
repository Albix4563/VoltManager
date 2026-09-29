using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HardwareProgram = VoltManager.HardwareService.Program;

namespace VoltManager.Tests;

public sealed class HardwareServiceProtocolTests
{
    [Fact]
    public async Task Named_pipe_protocol_round_trips_requests_and_reports_malformed_input()
    {
        string pipeName = "VoltManager.HardwareService.Tests." + Guid.NewGuid().ToString("N");
        Task server = HardwareProgram.RunServerAsync(
            pipeName,
            (method, _) => method switch
            {
                "ping" => new { ready = true },
                "shutdown" => new { success = true },
                _ => throw new InvalidOperationException("unknown_test_method"),
            },
            CancellationToken.None);

        using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync();
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 16 * 1024, leaveOpen: true);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { AutoFlush = true };

        await writer.WriteLineAsync("""{"id":"1","method":"ping","payload":{}}""");
        using (JsonDocument response = await ReadResponseAsync(reader))
        {
            Assert.Equal("1", response.RootElement.GetProperty("id").GetString());
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(response.RootElement.GetProperty("result").GetProperty("ready").GetBoolean());
        }

        await writer.WriteLineAsync("{ definitely-not-json");
        using (JsonDocument response = await ReadResponseAsync(reader))
        {
            Assert.Equal("", response.RootElement.GetProperty("id").GetString());
            Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(response.RootElement.GetProperty("error").GetString()));
        }

        await writer.WriteLineAsync("""{"id":"2","method":"unknown","payload":{}}""");
        using (JsonDocument response = await ReadResponseAsync(reader))
        {
            Assert.Equal("2", response.RootElement.GetProperty("id").GetString());
            Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("unknown_test_method", response.RootElement.GetProperty("error").GetString());
        }

        await writer.WriteLineAsync("""{"id":"3","method":"shutdown","payload":{}}""");
        using (JsonDocument response = await ReadResponseAsync(reader))
        {
            Assert.Equal("3", response.RootElement.GetProperty("id").GetString());
            Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        }

        await server;
    }

    [Theory]
    [InlineData(new[] { "--pipe", "test-pipe", "--parent", "123" }, true, "test-pipe", 123)]
    [InlineData(new[] { "--parent", "123" }, false, null, 0)]
    [InlineData(new[] { "--pipe", "test-pipe", "--parent", "0" }, false, null, 0)]
    public void Hardware_service_arguments_are_validated(
        string[] args,
        bool expected,
        string? expectedPipe,
        int expectedParent)
    {
        bool parsed = HardwareProgram.TryParseArguments(args, out var options);

        Assert.Equal(expected, parsed);
        if (!expected) return;
        Assert.Equal(expectedPipe, options.PipeName);
        Assert.Equal(expectedParent, options.ParentPid);
    }

    private static async Task<JsonDocument> ReadResponseAsync(StreamReader reader)
    {
        string? line = await reader.ReadLineAsync();
        Assert.False(string.IsNullOrWhiteSpace(line));
        return JsonDocument.Parse(line);
    }
}
