using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoltManager.Setup.Engine
{
    internal sealed class PreviewReleaseDownload : IDisposable
    {
        private FileStream? _heldFile;

        internal PreviewReleaseDownload(
            string exePath,
            string version,
            string stagingDirectory,
            FileStream heldFile)
        {
            ExePath = exePath;
            Version = version;
            StagingDirectory = stagingDirectory;
            _heldFile = heldFile;
        }

        public string ExePath { get; }
        public string Version { get; }
        public string StagingDirectory { get; }

        public void Dispose()
        {
            FileStream? heldFile = Interlocked.Exchange(ref _heldFile, null);
            heldFile?.Dispose();
        }
    }

    internal sealed class PreviewReleaseSelection
    {
        public string Version { get; set; } = "";
        public string ExeName { get; set; } = "";
        public string ExeUrl { get; set; } = "";
        public string ChecksumsUrl { get; set; } = "";
    }

    internal interface IPreviewReleaseClient
    {
        Task<PreviewReleaseDownload> DownloadLatestAsync(Action<double> onProgress, CancellationToken cancellationToken);
    }

    internal sealed class GitHubPreviewReleaseClient : IPreviewReleaseClient
    {
        private const string UpdateRepo = "Albix4563/VoltManager";
        private const int MaxChecksumsBytes = 64 * 1024;

        private readonly Func<HttpClient> _httpFactory;
        private readonly string _updatesRoot;
        private readonly Action<string> _errorLogger;
        private readonly Func<string, string> _runDirectoryFactory;

        internal GitHubPreviewReleaseClient(
            Func<HttpClient> httpFactory,
            string updatesRoot,
            Action<string>? errorLogger = null,
            Func<string, string>? runDirectoryFactory = null)
        {
            _httpFactory = httpFactory ?? throw new ArgumentNullException(nameof(httpFactory));
            _updatesRoot = string.IsNullOrWhiteSpace(updatesRoot)
                ? throw new ArgumentException("An updates directory is required.", nameof(updatesRoot))
                : updatesRoot;
            _errorLogger = errorLogger ?? SetupUpdateLog.Error;
            _runDirectoryFactory = runDirectoryFactory ?? SetupStaging.CreateProtectedRunDirectory;
        }

        internal static GitHubPreviewReleaseClient CreateDefault()
            => new GitHubPreviewReleaseClient(CreateHttpClient, SetupStaging.DefaultUpdatesRoot);

        public async Task<PreviewReleaseDownload> DownloadLatestAsync(
            Action<double> onProgress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress = onProgress ?? (_ => { });
            string? runDirectory = null;
            FileStream? heldFile = null;

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

                    PreviewReleaseSelection selection = SelectPreviewRelease(releasesJson);
                    string checksumsText = await DownloadChecksumsAsync(
                        http,
                        selection.ChecksumsUrl,
                        cancellationToken).ConfigureAwait(false);
                    IReadOnlyDictionary<string, string> checksums = ParseSha256Sums(checksumsText);
                    if (!checksums.TryGetValue(selection.ExeName, out string? expectedHash))
                    {
                        throw Reject(
                            "Preview rifiutata: SHA256SUMS non contiene un checksum per '" +
                            selection.ExeName + "'.");
                    }

                    try
                    {
                        runDirectory = _runDirectoryFactory(_updatesRoot);
                    }
                    catch (Exception ex)
                    {
                        throw Reject(
                            "Preview rifiutata: impossibile applicare ACL protette alla directory di staging. " +
                            ex.Message,
                            ex);
                    }

                    string destination = Path.Combine(
                        runDirectory,
                        Guid.NewGuid().ToString("N") + ".exe");
                    heldFile = await DownloadFileAsync(
                        http,
                        selection.ExeUrl,
                        destination,
                        onProgress,
                        cancellationToken).ConfigureAwait(false);

                    string actualHash = ComputeSha256(heldFile);
                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw Reject(
                            "Preview rifiutata: checksum SHA-256 non corrispondente per '" +
                            selection.ExeName + "'.");
                    }

                    var result = new PreviewReleaseDownload(
                        destination,
                        selection.Version,
                        runDirectory,
                        heldFile);
                    heldFile = null;
                    runDirectory = null;
                    return result;
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
            finally
            {
                heldFile?.Dispose();
                if (!string.IsNullOrWhiteSpace(runDirectory))
                    SetupStaging.DeleteRunDirectoryBestEffort(runDirectory, SetupUpdateLog.Warn);
            }
        }

        internal static PreviewReleaseSelection SelectPreviewRelease(string releasesJson)
        {
            object? parsed;
            try
            {
                parsed = SettingsJsonUpdater.CreateSerializer().DeserializeObject(releasesJson);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                throw new InvalidOperationException(I18n.T("err_preview_download") + " JSON release non valido.", ex);
            }

            if (parsed is not object[] releases)
                throw new InvalidOperationException(I18n.T("err_preview_download") + " JSON release non valido.");

            foreach (object releaseObject in releases)
            {
                if (releaseObject is not Dictionary<string, object?> release ||
                    !TryGetString(release, "tag_name", out string tagName))
                {
                    continue;
                }

                string version = tagName.StartsWith("v", StringComparison.OrdinalIgnoreCase)
                    ? tagName.Substring(1)
                    : tagName;
                if (version.IndexOf("-beta", StringComparison.OrdinalIgnoreCase) < 0 ||
                    version.IndexOf("-alpha", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                if (!release.TryGetValue("assets", out object? assetsValue) || assetsValue is not object[] assets)
                    throw new InvalidOperationException(I18n.T("err_preview_noasset"));

                string? exeName = null;
                string? exeUrl = null;
                string? checksumsUrl = null;
                foreach (object assetObject in assets)
                {
                    if (assetObject is not Dictionary<string, object?> asset)
                        continue;

                    TryGetString(asset, "name", out string name);
                    TryGetString(asset, "browser_download_url", out string url);

                    if (exeUrl == null &&
                        url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeName = name;
                        exeUrl = url;
                    }

                    if (checksumsUrl == null &&
                        string.Equals(name, "SHA256SUMS", StringComparison.Ordinal))
                    {
                        checksumsUrl = url;
                    }
                }

                if (string.IsNullOrWhiteSpace(exeUrl) || string.IsNullOrWhiteSpace(exeName))
                    throw new InvalidOperationException(I18n.T("err_preview_noasset"));
                if (string.IsNullOrWhiteSpace(checksumsUrl))
                {
                    string message = "Preview rifiutata: asset SHA256SUMS mancante nella release selezionata.";
                    SetupUpdateLog.Error(message);
                    throw new InvalidOperationException(message);
                }

                return new PreviewReleaseSelection
                {
                    Version = version,
                    ExeName = exeName!,
                    ExeUrl = exeUrl!,
                    ChecksumsUrl = checksumsUrl!,
                };
            }

            throw new InvalidOperationException(I18n.T("err_preview_norelease"));
        }

        internal static IReadOnlyDictionary<string, string> ParseSha256Sums(string text)
        {
            if (text == null)
                throw new ArgumentNullException(nameof(text));

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            string[] lines = text.Split(new[] { '\n' });
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index];
                if (line.EndsWith("\r", StringComparison.Ordinal))
                    line = line.Substring(0, line.Length - 1);

                bool isFinalEmptyLine = index == lines.Length - 1 && line.Length == 0;
                if (isFinalEmptyLine)
                    continue;
                if (line.Length < 67 || line[64] != ' ' || line[65] != ' ')
                    throw MalformedChecksums();

                string hash = line.Substring(0, 64);
                string name = line.Substring(66);
                if (name.Length == 0 || !IsHexSha256(hash) || result.ContainsKey(name))
                    throw MalformedChecksums();

                result.Add(name, hash.ToLowerInvariant());
            }

            if (result.Count == 0)
                throw MalformedChecksums();
            return result;
        }

        private InvalidOperationException Reject(string message, Exception? innerException = null)
        {
            _errorLogger(message);
            return innerException == null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, innerException);
        }

        private static InvalidOperationException MalformedChecksums()
        {
            const string message = "Preview rifiutata: SHA256SUMS non valido.";
            SetupUpdateLog.Error(message);
            return new InvalidOperationException(message);
        }

        private static bool TryGetString(
            Dictionary<string, object?> dictionary,
            string key,
            out string value)
        {
            if (dictionary.TryGetValue(key, out object? raw) && raw is string text)
            {
                value = text;
                return true;
            }

            value = "";
            return false;
        }

        private static bool IsHexSha256(string value)
        {
            if (value.Length != 64)
                return false;
            foreach (char c in value)
            {
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                bool upper = c >= 'A' && c <= 'F';
                if (!digit && !lower && !upper)
                    return false;
            }
            return true;
        }

        private static async Task<string> DownloadChecksumsAsync(
            HttpClient http,
            string url,
            CancellationToken cancellationToken)
        {
            using (HttpResponseMessage response = await http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > MaxChecksumsBytes)
                    throw MalformedChecksums();

                using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int total = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        total += read;
                        if (total > MaxChecksumsBytes)
                            throw MalformedChecksums();
                        memory.Write(buffer, 0, read);
                    }
                    return Encoding.UTF8.GetString(memory.ToArray());
                }
            }
        }

        private static async Task<FileStream> DownloadFileAsync(
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
                    using (var destinationStream = new FileStream(
                        destination,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        81920,
                        useAsync: true))
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

                var heldFile = new FileStream(
                    destination,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read);
                onProgress(100);
                return heldFile;
            }
            catch
            {
                try { if (File.Exists(destination)) File.Delete(destination); } catch { /* best-effort: failed download cleanup is non-fatal. */ }
                throw;
            }
        }

        private static string ComputeSha256(FileStream stream)
        {
            stream.Position = 0;
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(stream);
                stream.Position = 0;
                return BitConverter.ToString(hash).Replace("-", string.Empty);
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
