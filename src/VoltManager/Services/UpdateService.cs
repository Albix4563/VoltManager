using System.Net.Http;
using System.Reflection;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed class UpdateService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly GitHubUpdateClient _github;
    private readonly UpdateDownloadClient _downloader;
    private readonly Func<string> _currentVersion;
    private readonly bool _ownsHttpClient;
    private readonly object _releaseGate = new();
    // Asset URLs returned by our own release checks: a later (or failed) background
    // check must not invalidate a URL the UI already obtained and is about to download.
    private readonly HashSet<string> _knownReleaseDownloadUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, KnownInstallerAsset> _knownInstallerAssets = new(StringComparer.Ordinal);
    private int _disposed;

    public event Action<double>? DownloadProgress;

    public UpdateService(SettingsService settings)
        : this(settings, CreateHttpClient(), new SystemUpdateFileSystem(), TimeSpan.FromSeconds(10), null, true)
    {
    }

    internal UpdateService(SettingsService settings, TimeSpan downloadInactivityTimeout)
        : this(settings, CreateHttpClient(), new SystemUpdateFileSystem(), downloadInactivityTimeout, null, true)
    {
    }

    internal UpdateService(
        SettingsService settings,
        HttpClient http,
        IUpdateFileSystem fileSystem,
        TimeSpan downloadInactivityTimeout,
        Func<string>? currentVersion,
        bool ownsHttpClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _github = new GitHubUpdateClient(_http);
        _downloader = new UpdateDownloadClient(_http, fileSystem, downloadInactivityTimeout);
        _currentVersion = currentVersion ?? (() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        _ownsHttpClient = ownsHttpClient;
    }

    public string CurrentVersion => _currentVersion();

    public Task<UpdateInfo> CheckForUpdatesAsync()
        => CheckForUpdatesAsync(CancellationToken.None);

    public async Task<UpdateInfo> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string repo = _settings.Current.UpdateRepo;
        UpdateChannelPolicy policy = UpdateChannelPolicy.Parse(_settings.Current.AutoUpdates?.UpdateChannel);

        UpdateRemoteResult<GitHubReleaseRecord?> releaseResult = await _github.GetLatestReleaseAsync(
            repo, policy, cancellationToken);
        if (releaseResult.Status == UpdateRemoteStatus.RateLimited)
            return UpdateError("ratelimited", UpdatePresentation.RateLimited);
        if (releaseResult.Status == UpdateRemoteStatus.Offline)
            return UpdateError("offline", UpdatePresentation.Offline);
        if (releaseResult.Status == UpdateRemoteStatus.Error)
            return UpdateError("error", UpdatePresentation.HttpError(releaseResult.StatusCode ?? 0));

        GitHubReleaseRecord? release = releaseResult.Status == UpdateRemoteStatus.Ok
            ? releaseResult.Value
            : null;
        UpdateRemoteResult<List<CommitInfo>> commitsResult = await _github.GetCommitsAsync(
            repo, policy.Branch, cancellationToken);
        List<CommitInfo> commits = commitsResult.Status == UpdateRemoteStatus.Ok
            ? commitsResult.Value ?? new List<CommitInfo>()
            : new List<CommitInfo>();

        if (release == null)
        {
            if (commitsResult.Status == UpdateRemoteStatus.RateLimited)
                return UpdateError("ratelimited", UpdatePresentation.RateLimited);
            if (commitsResult.Status == UpdateRemoteStatus.Offline)
                return UpdateError("offline", UpdatePresentation.Offline);
            if (commitsResult.Status is UpdateRemoteStatus.Error or UpdateRemoteStatus.NotFound)
                return UpdateError("norelease", UpdatePresentation.NoRelease);

            return new UpdateInfo
            {
                Status = "ok",
                CurrentVersion = CurrentVersion,
                Commits = commits,
                Message = UpdatePresentation.NoPublishedRelease(policy.Branch),
            };
        }

        string latestVersion = policy.NormalizeVersion(release.TagName);
        GitHubReleaseAsset? installerAsset = release.Assets
            .FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        string? downloadUrl = installerAsset?.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            lock (_releaseGate)
            {
                _knownReleaseDownloadUrls.Add(downloadUrl);
                _knownInstallerAssets[downloadUrl] = new KnownInstallerAsset(
                    installerAsset!.Name,
                    release.Sha256SumsAssetUrl);
                if (!string.IsNullOrWhiteSpace(release.Sha256SumsAssetUrl))
                    _knownReleaseDownloadUrls.Add(release.Sha256SumsAssetUrl);
            }
        }
        bool updateAvailable = latestVersion.Length > 0 && CompareVersions(latestVersion, CurrentVersion) > 0;

        return new UpdateInfo
        {
            Status = "ok",
            UpdateAvailable = updateAvailable,
            LatestVersion = latestVersion,
            CurrentVersion = CurrentVersion,
            ReleaseNotes = release.Body,
            DownloadUrl = downloadUrl,
            Commits = commits,
            Message = updateAvailable
                ? UpdatePresentation.Available(policy, latestVersion, !string.IsNullOrWhiteSpace(downloadUrl))
                : UpdatePresentation.UpToDate,
        };
    }

    public Task<ReleaseHistory> GetReleaseHistoryAsync()
        => GetReleaseHistoryAsync(CancellationToken.None);

    public async Task<ReleaseHistory> GetReleaseHistoryAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        string repo = _settings.Current.UpdateRepo;
        UpdateChannelPolicy policy = UpdateChannelPolicy.Parse(_settings.Current.AutoUpdates?.UpdateChannel);
        UpdateRemoteResult<IReadOnlyList<GitHubReleaseRecord>> result = await _github.GetReleasesAsync(
            repo, 100, cancellationToken);

        if (result.Status == UpdateRemoteStatus.RateLimited)
            return HistoryError("ratelimited", UpdatePresentation.RateLimited);
        if (result.Status == UpdateRemoteStatus.Offline)
            return HistoryError("offline", UpdatePresentation.Offline);
        if (result.Status == UpdateRemoteStatus.Error)
            return HistoryError("error", UpdatePresentation.HttpError(result.StatusCode ?? 0));

        var releases = new List<ReleaseEntry>();
        if (result.Status == UpdateRemoteStatus.Ok)
        {
            foreach (GitHubReleaseRecord release in result.Value ?? Array.Empty<GitHubReleaseRecord>())
            {
                if (!policy.Matches(release))
                    continue;
                string version = policy.NormalizeVersion(release.TagName);
                releases.Add(new ReleaseEntry
                {
                    Version = version,
                    Name = release.Name,
                    Date = release.PublishedAt ?? "",
                    Notes = release.Body,
                    HtmlUrl = release.HtmlUrl,
                    Prerelease = release.Prerelease,
                    // The assembly version carries no channel suffix (1.0.429 for tag v1.0.429-alpha):
                    // match the release this build came from on the numeric core only.
                    IsCurrent = version.Length > 0 && CompareVersions(VersionCore(version), CurrentVersion) == 0,
                });
            }
        }

        if (releases.Count > 0)
            return new ReleaseHistory { Status = "ok", CurrentVersion = CurrentVersion, Releases = releases };

        UpdateRemoteResult<List<CommitInfo>> commitsResult = await _github.GetCommitsAsync(
            repo, policy.Branch, cancellationToken);
        List<CommitInfo> commits = commitsResult.Status == UpdateRemoteStatus.Ok
            ? commitsResult.Value ?? new List<CommitInfo>()
            : new List<CommitInfo>();
        return new ReleaseHistory
        {
            Status = "norelease",
            CurrentVersion = CurrentVersion,
            Commits = commits,
            Message = commitsResult.Status == UpdateRemoteStatus.Ok
                ? UpdatePresentation.NoPublishedRelease(policy.Branch)
                : UpdatePresentation.NoRelease,
        };
    }

    public Task<VerifiedUpdateDownload> DownloadUpdateAsync(string url)
        => DownloadUpdateAsync(url, CancellationToken.None);

    public Task<VerifiedUpdateDownload> DownloadUpdateAsync(string url, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        KnownInstallerAsset asset;
        lock (_releaseGate)
        {
            if (!_knownInstallerAssets.TryGetValue(url, out KnownInstallerAsset? known))
                throw new InvalidOperationException("URL aggiornamento non autorizzato o metadati release mancanti.");
            asset = known;
        }
        return _downloader.DownloadAsync(
            url,
            asset.AssetName,
            asset.Sha256SumsUrl,
            progress => DownloadProgress?.Invoke(progress),
            cancellationToken);
    }

    public bool IsKnownReleaseAssetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (_releaseGate)
            return _knownReleaseDownloadUrls.Contains(url);
    }

    internal static string VersionCore(string version)
    {
        int end = version.IndexOfAny(new[] { '-', '+' });
        return end >= 0 ? version[..end] : version;
    }

    public static int CompareVersions(string a, string b)
    {
        static (string[] Core, string[]? PreRelease) Parse(string version)
        {
            version ??= string.Empty;
            if (version.Length > 0 && (version[0] == 'v' || version[0] == 'V'))
                version = version[1..];

            int metadataIndex = version.IndexOf('+');
            if (metadataIndex >= 0)
                version = version[..metadataIndex];

            int prereleaseIndex = version.IndexOf('-');
            string core = prereleaseIndex >= 0 ? version[..prereleaseIndex] : version;
            string[]? prerelease = prereleaseIndex >= 0
                ? version[(prereleaseIndex + 1)..].Split('.')
                : null;
            return (core.Split('.'), prerelease);
        }

        static bool IsNumeric(string value)
            => value.Length > 0 && value.All(c => c is >= '0' and <= '9');

        static string NumericValueOrZero(string value)
            => IsNumeric(value) ? value : "0";

        static int CompareNumeric(string left, string right)
        {
            left = left.TrimStart('0');
            right = right.TrimStart('0');
            if (left.Length == 0) left = "0";
            if (right.Length == 0) right = "0";

            int lengthComparison = left.Length.CompareTo(right.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.Compare(left, right, StringComparison.Ordinal);
        }

        static int ComparePrereleaseIdentifier(string left, string right)
        {
            bool leftNumeric = IsNumeric(left);
            bool rightNumeric = IsNumeric(right);
            if (leftNumeric && rightNumeric)
                return CompareNumeric(left, right);
            if (leftNumeric != rightNumeric)
                return leftNumeric ? -1 : 1;
            return string.Compare(left, right, StringComparison.Ordinal);
        }

        (string[] Core, string[]? PreRelease) pa = Parse(a);
        (string[] Core, string[]? PreRelease) pb = Parse(b);

        for (int i = 0; i < Math.Max(pa.Core.Length, pb.Core.Length); i++)
        {
            string xa = i < pa.Core.Length ? NumericValueOrZero(pa.Core[i]) : "0";
            string xb = i < pb.Core.Length ? NumericValueOrZero(pb.Core[i]) : "0";
            int coreComparison = CompareNumeric(xa, xb);
            if (coreComparison != 0)
                return coreComparison;
        }

        if (pa.PreRelease is null || pb.PreRelease is null)
        {
            if (pa.PreRelease is null && pb.PreRelease is null)
                return 0;
            return pa.PreRelease is null ? 1 : -1;
        }

        int sharedLength = Math.Min(pa.PreRelease.Length, pb.PreRelease.Length);
        for (int i = 0; i < sharedLength; i++)
        {
            int identifierComparison = ComparePrereleaseIdentifier(pa.PreRelease[i], pb.PreRelease[i]);
            if (identifierComparison != 0)
                return identifierComparison;
        }

        return pa.PreRelease.Length.CompareTo(pb.PreRelease.Length);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private UpdateInfo UpdateError(string status, string message)
        => new() { Status = status, CurrentVersion = CurrentVersion, Message = message };

    private ReleaseHistory HistoryError(string status, string message)
        => new() { Status = status, CurrentVersion = CurrentVersion, Message = message };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed record KnownInstallerAsset(string AssetName, string? Sha256SumsUrl);

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VoltManager");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
