using System.Net;
using System.Net.Http;
using VoltManager.Setup.Engine;

namespace VoltManager.Setup.Tests;

public sealed class PreviewReleaseIntegrityTests
{
    private const string AssetName = "VoltManagerSetup-2.0.0-beta.exe";

    [Fact]
    public async Task Hash_mismatch_is_refused_logged_and_staging_directory_is_deleted()
    {
        string root = CreateTempDirectory();
        var errors = new List<string>();
        var handler = new SequenceHandler(
            JsonResponse(ReleaseJson(includeChecksums: true)),
            TextResponse(new string('0', 64) + "  " + AssetName + "\n"),
            TextResponse("payload"));
        var client = new GitHubPreviewReleaseClient(
            () => new HttpClient(handler, false),
            root,
            errors.Add,
            CreateUnprotectedRunDirectory);

        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadLatestAsync(_ => { }, CancellationToken.None));

            Assert.Contains("checksum SHA-256 non corrispondente", error.Message);
            Assert.Contains(errors, message => message.Contains("checksum SHA-256 non corrispondente"));
            Assert.Empty(Directory.GetDirectories(root));
            Assert.Equal(3, handler.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Missing_sha256sums_asset_is_refused_before_downloading_exe()
    {
        string root = CreateTempDirectory();
        var handler = new SequenceHandler(JsonResponse(ReleaseJson(includeChecksums: false)));
        var client = new GitHubPreviewReleaseClient(
            () => new HttpClient(handler, false),
            root,
            runDirectoryFactory: CreateUnprotectedRunDirectory);

        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadLatestAsync(_ => { }, CancellationToken.None));

            Assert.Contains("SHA256SUMS mancante", error.Message);
            Assert.Empty(Directory.GetDirectories(root));
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Missing_selected_asset_entry_is_refused_before_downloading_exe()
    {
        string root = CreateTempDirectory();
        var errors = new List<string>();
        var handler = new SequenceHandler(
            JsonResponse(ReleaseJson(includeChecksums: true)),
            TextResponse(new string('a', 64) + "  different.exe\n"));
        var client = new GitHubPreviewReleaseClient(
            () => new HttpClient(handler, false),
            root,
            errors.Add,
            CreateUnprotectedRunDirectory);

        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadLatestAsync(_ => { }, CancellationToken.None));

            Assert.Contains("non contiene un checksum", error.Message);
            Assert.Contains(errors, message => message.Contains("non contiene un checksum"));
            Assert.Empty(Directory.GetDirectories(root));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Sha256sums_larger_than_64_kib_is_refused_before_staging()
    {
        string root = CreateTempDirectory();
        var handler = new SequenceHandler(
            JsonResponse(ReleaseJson(includeChecksums: true)),
            TextResponse(new string('a', (64 * 1024) + 1)));
        var client = new GitHubPreviewReleaseClient(
            () => new HttpClient(handler, false),
            root,
            runDirectoryFactory: CreateUnprotectedRunDirectory);

        try
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DownloadLatestAsync(_ => { }, CancellationToken.None));

            Assert.Contains("SHA256SUMS non valido", error.Message);
            Assert.Empty(Directory.GetDirectories(root));
            Assert.Equal(2, handler.Calls);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string ReleaseJson(bool includeChecksums)
    {
        string checksums = includeChecksums
            ? ",{\"name\":\"SHA256SUMS\",\"browser_download_url\":\"https://example.test/SHA256SUMS\"}"
            : "";
        return "[{\"tag_name\":\"v2.0.0-beta\",\"prerelease\":true,\"assets\":[" +
               "{\"name\":\"" + AssetName + "\",\"browser_download_url\":\"https://example.test/setup.exe\"}" +
               checksums + "]}]";
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static HttpResponseMessage TextResponse(string text)
        => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };

    private static string CreateTempDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "VoltManagerPreviewIntegrityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static string CreateUnprotectedRunDirectory(string root)
    {
        string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage[] _responses;
        public int Calls { get; private set; }

        public SequenceHandler(params HttpResponseMessage[] responses)
            => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Calls >= _responses.Length)
                throw new InvalidOperationException("Unexpected HTTP request: " + request.RequestUri);
            HttpResponseMessage response = _responses[Calls++];
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
