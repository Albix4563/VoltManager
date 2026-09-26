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
        string? downloadUrl = release.Assets
            .FirstOrDefault(asset => asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?.DownloadUrl;
        if (!string.IsNullOrWhiteSpace(downloadUrl))
        {
            lock (_releaseGate)
                _knownReleaseDownloadUrls.Add(downloadUrl);
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
                    IsCurrent = version.Length > 0 && CompareVersions(version, CurrentVersion) == 0,
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

    public Task<string> DownloadUpdateAsync(string url)
        => DownloadUpdateAsync(url, CancellationToken.None);

    public Task<string> DownloadUpdateAsync(string url, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _downloader.DownloadAsync(url, progress => DownloadProgress?.Invoke(progress), cancellationToken);
    }

    public bool IsKnownReleaseAssetUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        lock (_releaseGate)
            return _knownReleaseDownloadUrls.Contains(url);
    }

    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string v) => v.Split('-')[0].Split('.')
            .Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        int[] pa = Parse(a);
        int[] pb = Parse(b);
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int xa = i < pa.Length ? pa[i] : 0;
            int xb = i < pb.Length ? pb[i] : 0;
            if (xa != xb)
                return xa.CompareTo(xb);
        }
        return 0;
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

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("VoltManager");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
}
