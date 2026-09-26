using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class UpdatePipelineTests
{
    [Theory]
    [InlineData("stable", "v2.0.0", false, true)]
    [InlineData("preview", "v2.0.0-beta.1", true, true)]
    [InlineData("preview", "v2.0.0-alpha.1", true, false)]
    [InlineData("dev", "v2.0.0-alpha.1", true, true)]
    [InlineData("dev", "v2.0.0-beta.1", true, false)]
    public void Channel_policy_matches_expected_release(string channel, string tag, bool prerelease, bool expected)
    {
        UpdateChannelPolicy policy = UpdateChannelPolicy.Parse(channel);
        var release = new GitHubReleaseRecord(tag, null, null, null, null, prerelease, Array.Empty<GitHubReleaseAsset>());

        Assert.Equal(expected, policy.Matches(release));
    }

    [Theory]
    [InlineData("stable", "main")]
    [InlineData("preview", "Preview")]
    [InlineData("dev", "Dev")]
    public void Channel_policy_preserves_branch_mapping(string channel, string branch)
        => Assert.Equal(branch, UpdateChannelPolicy.Parse(channel).Branch);

    [Fact]
    public async Task Github_client_reports_rate_limit_and_disposes_response_content()
    {
        var content = new TrackingContent("{}");
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)429) { Content = content }));
        var client = new GitHubUpdateClient(http);

        UpdateRemoteResult<GitHubReleaseRecord?> result = await client.GetLatestReleaseAsync(
            "owner/repo", UpdateChannelPolicy.Parse("stable"), CancellationToken.None);

        Assert.Equal(UpdateRemoteStatus.RateLimited, result.Status);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Github_client_reports_offline_without_throwing()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        var client = new GitHubUpdateClient(http);

        UpdateRemoteResult<GitHubReleaseRecord?> result = await client.GetLatestReleaseAsync(
            "owner/repo", UpdateChannelPolicy.Parse("stable"), CancellationToken.None);

        Assert.Equal(UpdateRemoteStatus.Offline, result.Status);
    }

    [Fact]
    public async Task Service_reports_available_release_without_installer_asset_explicitly()
    {
        const string releaseJson = """
        {"tag_name":"v99.0.0","name":"Release","body":"notes","published_at":"2026-09-21T00:00:00Z","html_url":"https://example/release","prerelease":false,"assets":[]}
        """;
        const string commitsJson = "[]";
        using var http = new HttpClient(new StubHandler(request =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                    ? releaseJson
                    : commitsJson, Encoding.UTF8, "application/json"),
            }));
        var fs = new MemoryUpdateFileSystem();
        using UpdateService service = CreateService(http, fs, currentVersion: "1.0.0");

        UpdateInfo info = await service.CheckForUpdatesAsync();

        Assert.Equal("ok", info.Status);
        Assert.True(info.UpdateAvailable);
        Assert.Null(info.DownloadUrl);
        Assert.Contains("installer", info.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_only_approves_installer_urls_from_its_own_release_checks()
    {
        const string installerUrl = "https://github.com/owner/repo/releases/download/v99.0.0/VoltManagerSetup.exe";
        bool offline = false;
        const string releaseJson = """
        {"tag_name":"v99.0.0","name":"Release","body":"notes","published_at":"2026-09-21T00:00:00Z","html_url":"https://github.com/owner/repo/releases/tag/v99.0.0","prerelease":false,"assets":[{"name":"VoltManagerSetup.exe","browser_download_url":"https://github.com/owner/repo/releases/download/v99.0.0/VoltManagerSetup.exe"}]}
        """;
        using var http = new HttpClient(new StubHandler(request =>
            offline
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)
                        ? releaseJson
                        : "[]", Encoding.UTF8, "application/json"),
                }));
        using UpdateService service = CreateService(http, new MemoryUpdateFileSystem(), "1.0.0");

        Assert.False(service.IsKnownReleaseAssetUrl(installerUrl));
        UpdateInfo info = await service.CheckForUpdatesAsync();

        Assert.Equal(installerUrl, info.DownloadUrl);
        Assert.True(service.IsKnownReleaseAssetUrl(installerUrl));
        Assert.False(service.IsKnownReleaseAssetUrl("https://evil.example/VoltManagerSetup.exe"));

        // A later failing background check must not revoke the URL the UI already holds.
        offline = true;
        try { await service.CheckForUpdatesAsync(); } catch { }
        Assert.True(service.IsKnownReleaseAssetUrl(installerUrl));
    }

    [Fact]
    public async Task Downloader_timeout_removes_partial_file()
    {
        var fs = new MemoryUpdateFileSystem();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingReadStream()),
        }));
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAsync<TimeoutException>(() => downloader.DownloadAsync("https://example/update.exe", null, CancellationToken.None));

        Assert.True(fs.DeleteCalled);
        Assert.False(fs.Exists(fs.UpdatePath));
    }

    [Fact]
    public async Task Downloader_cancellation_propagates_and_removes_partial_file()
    {
        var fs = new MemoryUpdateFileSystem();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingReadStream()),
        }));
        var downloader = new UpdateDownloadClient(http, fs, TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync("https://example/update.exe", null, cts.Token));

        Assert.True(fs.DeleteCalled);
        Assert.False(fs.Exists(fs.UpdatePath));
    }

    private static UpdateService CreateService(HttpClient http, IUpdateFileSystem fs, string currentVersion)
    {
        string settingsPath = Path.Combine(Path.GetTempPath(), $"voltmanager-update-pipeline-{Guid.NewGuid():N}.json");
        var settings = new SettingsService(settingsPath);
        return new UpdateService(settings, http, fs, TimeSpan.FromMilliseconds(100), () => currentVersion, ownsHttpClient: false);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) => _send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_send(request));
    }

    private sealed class TrackingContent : StringContent
    {
        public TrackingContent(string content) : base(content) { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class StallingReadStream : Stream
    {
        private bool _sentInitial;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentInitial)
            {
                _sentInitial = true;
                buffer.Span[0] = 1;
                return 1;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class MemoryUpdateFileSystem : IUpdateFileSystem
    {
        private MemoryStream? _stream;
        public string UpdatePath => "memory://VoltManagerUpdate.exe";
        public bool DeleteCalled { get; private set; }
        public string GetTempFilePath(string fileName) => UpdatePath;
        public Stream CreateFile(string path) => _stream = new MemoryStream();
        public bool Exists(string path) => _stream != null;
        public void Delete(string path)
        {
            DeleteCalled = true;
            _stream?.Dispose();
            _stream = null;
        }
    }
}
