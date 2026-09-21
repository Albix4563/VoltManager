using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests
{
    public sealed class PreviewReleaseClientTests
    {
        [Fact]
        public async Task Client_selects_beta_asset_downloads_it_and_disposes_http_content()
        {
            string root = CreateTempDirectory();
            var releases = new TrackingContent(
                "[{\"tag_name\":\"v1.4.0-beta\",\"assets\":[{\"browser_download_url\":\"https://example.test/setup.exe\"}]}]");
            var asset = new TrackingContent("payload");
            var handler = new SequenceHandler(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = releases },
                new HttpResponseMessage(HttpStatusCode.OK) { Content = asset });
            var client = new GitHubPreviewReleaseClient(() => new HttpClient(handler, false), root);

            try
            {
                PreviewReleaseDownload result = await client.DownloadLatestAsync(_ => { }, CancellationToken.None);

                Assert.Equal("1.4.0-beta", result.Version);
                Assert.Equal("payload", File.ReadAllText(result.ExePath));
                Assert.True(releases.Disposed);
                Assert.True(asset.Disposed);
                Assert.Equal(2, handler.Calls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        [Fact]
        public async Task Client_honors_caller_cancellation_before_network_work()
        {
            string root = CreateTempDirectory();
            var handler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK));
            var client = new GitHubPreviewReleaseClient(() => new HttpClient(handler, false), root);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            try
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => client.DownloadLatestAsync(_ => { }, cts.Token));
                Assert.Equal(0, handler.Calls);
            }
            finally
            {
                Cleanup(root);
            }
        }

        private static string CreateTempDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "VoltManagerPreviewTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void Cleanup(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly HttpResponseMessage[] _responses;
            public int Calls { get; private set; }

            public SequenceHandler(params HttpResponseMessage[] responses) => _responses = responses;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HttpResponseMessage response = _responses[Math.Min(Calls, _responses.Length - 1)];
                Calls++;
                response.RequestMessage = request;
                return Task.FromResult(response);
            }
        }

        private sealed class TrackingContent : StringContent
        {
            public TrackingContent(string value) : base(value) { }
            public bool Disposed { get; private set; }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
