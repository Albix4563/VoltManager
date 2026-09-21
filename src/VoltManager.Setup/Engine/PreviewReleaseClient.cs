using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoltManager.Setup.Engine
{
    internal sealed class PreviewReleaseDownload
    {
        public string ExePath { get; set; } = "";
        public string Version { get; set; } = "";
    }

    internal interface IPreviewReleaseClient
    {
        Task<PreviewReleaseDownload> DownloadLatestAsync(Action<double> onProgress, CancellationToken cancellationToken);
    }

    internal sealed class GitHubPreviewReleaseClient : IPreviewReleaseClient
    {
        private const string UpdateRepo = "Albix4563/power_efficency";

        private static readonly Regex TagRegex = new Regex(
            "\"tag_name\"\\s*:\\s*\"v?(?<v>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex AssetUrlRegex = new Regex(
            "\"browser_download_url\"\\s*:\\s*\"(?<u>[^\"]+\\.exe)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Func<HttpClient> _httpFactory;
        private readonly string _tempDirectory;

        internal GitHubPreviewReleaseClient(Func<HttpClient> httpFactory, string tempDirectory)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _tempDirectory = string.IsNullOrWhiteSpace(tempDirectory)
                ? throw new ArgumentException("A temporary directory is required.", nameof(tempDirectory))
                : tempDirectory;
        }

        internal static GitHubPreviewReleaseClient CreateDefault()
            => new GitHubPreviewReleaseClient(CreateHttpClient, Path.GetTempPath());

        public async Task<PreviewReleaseDownload> DownloadLatestAsync(
            Action<double> onProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress = onProgress ?? (_ => { });

            try
            {
                using (HttpClient http = _httpFactory())
                using (HttpResponseMessage releasesResponse = await http.GetAsync(
                    "https://api.github.com/repos/" + UpdateRepo + "/releases?per_page=20",
                    HttpCompletionOption.ResponseContentRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    releasesResponse.EnsureSuccessStatusCode();
                    string releasesJson = await releasesResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    MatchCollection tags = TagRegex.Matches(releasesJson);
                    Match? chosen = null;
                    foreach (Match match in tags)
                    {
                        string version = match.Groups["v"].Value;
                        if (version.IndexOf("-beta", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            version.IndexOf("-alpha", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            chosen = match;
                            break;
                        }
                    }

                    if (chosen == null)
                        throw new InvalidOperationException(I18n.T("err_preview_norelease"));

                    int scopeEnd = releasesJson.Length;
                    foreach (Match match in tags)
                    {
                        if (match.Index > chosen.Index)
                        {
                            scopeEnd = match.Index;
                            break;
                        }
                    }

                    string scope = releasesJson.Substring(chosen.Index, scopeEnd - chosen.Index);
                    Match asset = AssetUrlRegex.Match(scope);
                    if (!asset.Success)
                        throw new InvalidOperationException(I18n.T("err_preview_noasset"));

                    Directory.CreateDirectory(_tempDirectory);
                    string destination = Path.Combine(_tempDirectory, "VoltManagerPreviewSetup.exe");
                    await DownloadFileAsync(
                        http,
                        asset.Groups["u"].Value,
                        destination,
                        onProgress,
                        cancellationToken).ConfigureAwait(false);

                    return new PreviewReleaseDownload
                    {
                        ExePath = destination,
                        Version = chosen.Groups["v"].Value,
                    };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(I18n.T("err_preview_download") + " " + ex.Message, ex);
            }
        }

        private static async Task DownloadFileAsync(
            HttpClient http,
            string url,
            string destination,
            Action<double> onProgress,
            CancellationToken cancellationToken)
        {
            try
            {
                using (HttpResponseMessage response = await http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    long expectedLength = response.Content.Headers.ContentLength ?? -1;
                    long received = 0;

                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (Stream destinationStream = File.Create(destination))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await destinationStream.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            received += read;
                            if (expectedLength > 0)
                                onProgress(Math.Min(99.9, Math.Round(received * 100.0 / expectedLength, 1)));
                        }

                        if (expectedLength >= 0 && received != expectedLength)
                            throw new EndOfStreamException("Preview download incomplete.");

                        await destinationStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                onProgress(100);
            }
            catch
            {
                try
                {
                    if (File.Exists(destination))
                        File.Delete(destination);
                }
                catch
                {
                    // Preserve the original download error.
                }
                throw;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("VoltManager-Setup");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }
    }
}
