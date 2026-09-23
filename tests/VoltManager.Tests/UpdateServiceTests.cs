using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;
using System.Text;
using VoltManager.Services;

namespace VoltManager.Tests;

public class UpdateServiceTests
{
    [Fact]
    public async Task Download_progress_does_not_report_100_before_body_completes()
    {
        await using var server = StallingHttpServer.Start(totalBytes: 10_000, initialBytes: 9_996);
        var service = CreateService();
        var nearComplete = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.DownloadProgress += pct =>
        {
            if (pct >= 99.9)
                nearComplete.TrySetResult(pct);
        };

        string? downloadedPath = null;
        try
        {
            Task<string> download = service.DownloadUpdateAsync(server.Url);
            double reported = await nearComplete.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(reported < 100, $"Progress reached {reported}% before the response body completed.");

            server.ReleaseRemainder();
            downloadedPath = await download.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(File.Exists(downloadedPath));
        }
        finally
        {
            server.ReleaseRemainder();
            DeleteIfExists(downloadedPath ?? Path.Combine(Path.GetTempPath(), "VoltManagerUpdate.exe"));
        }
    }

    [Fact]
    public async Task Download_times_out_when_response_body_stops_making_progress()
    {
        await using var server = StallingHttpServer.Start(totalBytes: 10_000, initialBytes: 100);
        var service = CreateService(TimeSpan.FromMilliseconds(150));

        Task<string> download = service.DownloadUpdateAsync(server.Url);

        try
        {
            await server.InitialBytesSent.WaitAsync(TimeSpan.FromSeconds(10));
            Task completed = await Task.WhenAny(download, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(download, completed);
            await Assert.ThrowsAsync<TimeoutException>(async () => await download);
        }
        finally
        {
            server.ReleaseRemainder();
            try { await download.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            DeleteIfExists(Path.Combine(Path.GetTempPath(), "VoltManagerUpdate.exe"));
        }
    }

    private static UpdateService CreateService(TimeSpan? downloadInactivityTimeout = null)
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"voltmanager-update-test-{Guid.NewGuid():N}.json");
        var settings = new SettingsService(settingsPath);
        return downloadInactivityTimeout is { } timeout
            ? new UpdateService(settings, timeout)
            : new UpdateService(settings);
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup for a temp test artifact.
        }
    }

    private sealed class StallingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _initialBytesSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int _totalBytes;
        private readonly int _initialBytes;
        private readonly Task _serveTask;

        private StallingHttpServer(TcpListener listener, int totalBytes, int initialBytes)
        {
            _listener = listener;
            _totalBytes = totalBytes;
            _initialBytes = initialBytes;
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/VoltManagerUpdate.exe";
            _serveTask = ServeAsync();
        }

        public string Url { get; }
        public Task InitialBytesSent => _initialBytesSent.Task;

        public static StallingHttpServer Start(int totalBytes, int initialBytes)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new StallingHttpServer(listener, totalBytes, initialBytes);
        }

        public void ReleaseRemainder() => _release.TrySetResult(true);

        private async Task ServeAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using NetworkStream stream = client.GetStream();
                await ReadRequestHeadersAsync(stream, _stop.Token);

                byte[] headers = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Length: {_totalBytes}\r\nContent-Type: application/octet-stream\r\nConnection: keep-alive\r\n\r\n");
                await stream.WriteAsync(headers, _stop.Token);
                await stream.WriteAsync(new byte[_initialBytes], _stop.Token);
                await stream.FlushAsync(_stop.Token);
                _initialBytesSent.TrySetResult(true);

                await _release.Task.WaitAsync(_stop.Token);

                int remaining = _totalBytes - _initialBytes;
                if (remaining > 0)
                    await stream.WriteAsync(new byte[remaining], _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
        }

        private static async Task ReadRequestHeadersAsync(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>();
            var buffer = new byte[512];
            while (bytes.Count < 16_384)
            {
                int read = await stream.ReadAsync(buffer, ct);
                if (read == 0) return;
                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == '\r' && bytes[count - 3] == '\n' &&
                    bytes[count - 2] == '\r' && bytes[count - 1] == '\n')
                    return;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _release.TrySetCanceled(_stop.Token);
            _listener.Stop();
            try { await _serveTask; } catch (SocketException) when (_stop.IsCancellationRequested) { }
            _stop.Dispose();
        }
    }
}
