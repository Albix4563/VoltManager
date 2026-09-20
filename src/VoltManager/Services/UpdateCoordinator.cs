using VoltManager.Models;

namespace VoltManager.Services;

public sealed class UpdateCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly RestartableLifecycle _lifecycle = new();
    private readonly Func<Task<UpdateInfo>> _check;
    private readonly Func<bool> _protectedWorkload;
    private readonly Func<TimeSpan, Action, IDisposable> _createTimer;
    private readonly SettingsService? _settings;
    private readonly Func<string, CancellationToken, Task<string>>? _download;
    private IDisposable? _timer;
    private Task? _inflightCheck;
    private string? _deferredInstallUrl;
    private bool _deferredCheck;
    private bool _disposed;

    public UpdateCoordinator(
        UpdateService updates,
        SettingsService settings,
        Func<bool> protectedWorkload)
        : this(
            updates.CheckForUpdatesAsync,
            protectedWorkload,
            CreateRecurringTimer,
            settings,
            (url, _) => updates.DownloadUpdateAsync(url))
    {
    }

    private UpdateCoordinator(
        Func<Task<UpdateInfo>> check,
        Func<bool> protectedWorkload,
        Func<TimeSpan, Action, IDisposable> createTimer,
        SettingsService? settings = null,
        Func<string, CancellationToken, Task<string>>? download = null)
    {
        _check = check;
        _protectedWorkload = protectedWorkload;
        _createTimer = createTimer;
        _settings = settings;
        _download = download;
    }

    internal static UpdateCoordinator ForTest(
        Func<Task<UpdateInfo>> check,
        Func<bool> protectedWorkload,
        Func<TimeSpan, Action, IDisposable> createTimer)
        => new(check, protectedWorkload, createTimer);

    public event Action<UpdateInfo>? UpdateAvailable;
    public event Action<UpdateInfo>? PromptRequested;
    public event Action<string>? InstallRequested;

    public void Start()
    {
        CancellationToken epoch;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UpdateCoordinator));
            if (_lifecycle.IsStarted)
                return;

            epoch = _lifecycle.Start();
            _timer = _createTimer(UpdateSchedulePolicy.AutomaticCheckInterval, () =>
            {
                if (_lifecycle.IsCurrent(epoch))
                    _ = CheckNowAsync(automatic: true);
            });
        }
    }

    public void Stop()
    {
        IDisposable? timer;
        lock (_gate)
        {
            if (!_lifecycle.Stop())
                return;
            timer = _timer;
            _timer = null;
            _inflightCheck = null;
        }
        try { timer?.Dispose(); }
        catch (Exception ex) { Logger.Error("Update timer disposal failed", ex); }
    }

    public Task CheckNowAsync(bool automatic, CancellationToken cancellationToken = default)
    {
        CancellationToken epoch;
        lock (_gate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(UpdateCoordinator));
            if (!_lifecycle.IsStarted)
                return Task.CompletedTask;

            if (automatic && !AutomaticCheckAllowed())
                return Task.CompletedTask;

            if (_protectedWorkload())
            {
                if (automatic)
                    _deferredCheck = true;
                return Task.CompletedTask;
            }

            if (_inflightCheck is { IsCompleted: false })
                return _inflightCheck;

            epoch = CurrentEpoch();
            _inflightCheck = RunCheckAsync(automatic, epoch, cancellationToken);
            return _inflightCheck;
        }
    }

    public void DeferInstall(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        lock (_gate)
            _deferredInstallUrl = url.Trim();
        Logger.Info("Update install deferred until protected workload ends.");
    }

    public bool HasDeferredInstall
    {
        get
        {
            lock (_gate)
                return !string.IsNullOrWhiteSpace(_deferredInstallUrl);
        }
    }

    public string? TakeDeferredInstall()
    {
        lock (_gate)
        {
            string? url = _deferredInstallUrl;
            _deferredInstallUrl = null;
            return url;
        }
    }

    public async Task NotifyProtectedWorkloadChangedAsync(bool active)
    {
        if (active || _protectedWorkload() || !_lifecycle.IsStarted)
            return;

        string? deferredUrl;
        bool resumeCheck;
        lock (_gate)
        {
            deferredUrl = _deferredInstallUrl;
            if (deferredUrl != null)
                _deferredInstallUrl = null;
            resumeCheck = _deferredCheck;
            _deferredCheck = false;
        }

        if (!string.IsNullOrWhiteSpace(deferredUrl) && _download != null)
        {
            await PrepareInstallAsync(deferredUrl, _download).ConfigureAwait(false);
            return;
        }

        if (resumeCheck)
            await CheckNowAsync(automatic: true).ConfigureAwait(false);
    }

    public async Task PrepareInstallAsync(
        string url,
        Func<string, CancellationToken, Task<string>> download,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !_lifecycle.IsStarted)
            return;

        CancellationToken epoch = CurrentEpoch();
        if (_protectedWorkload())
        {
            DeferInstall(url);
            return;
        }

        string path = await download(url, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_lifecycle.IsCurrent(epoch))
            return;
        if (_protectedWorkload())
        {
            DeferInstall(url);
            return;
        }

        InstallRequested?.Invoke(path);
    }

    public void Snooze(int minutes)
    {
        if (_settings == null) return;
        minutes = UpdateSchedulePolicy.NormalizeSnoozeMinutes(minutes);
        DateTime until = DateTime.UtcNow.AddMinutes(minutes);
        _settings.Update(state => state.AutoUpdates.SnoozedUntilUtc = until);
    }

    public void SkipVersion(string? version)
    {
        if (_settings == null) return;
        string normalized = NormalizeVersion(version);
        if (normalized.Length == 0) return;
        _settings.Update(state =>
        {
            state.AutoUpdates.SkippedVersion = normalized;
            state.AutoUpdates.SnoozedUntilUtc = null;
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _lifecycle.Dispose();
    }

    private async Task RunCheckAsync(bool automatic, CancellationToken epoch, CancellationToken cancellationToken)
    {
        try
        {
            UpdateInfo info = await _check().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_lifecycle.IsCurrent(epoch) || !info.UpdateAvailable || string.IsNullOrWhiteSpace(info.DownloadUrl))
                return;
            if (IsSuppressed(info, automatic))
                return;

            if (_protectedWorkload())
            {
                if (automatic && ShouldInstallSilently())
                    DeferInstall(info.DownloadUrl);
                return;
            }

            UpdateAvailable?.Invoke(info);
            if (automatic && !ShouldInstallSilently() && _lifecycle.IsCurrent(epoch))
                PromptRequested?.Invoke(info);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (automatic)
        {
            Logger.Warn("Automatic update check failed: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (_inflightCheck?.IsCompleted != false)
                    _inflightCheck = null;
            }
        }
    }

    private bool AutomaticCheckAllowed()
        => _settings == null || UpdateSchedulePolicy.IsAutomaticCheckAllowed(_settings.Current.AutoUpdates, DateTime.UtcNow);

    private bool ShouldInstallSilently()
        => _settings?.Current.AutoUpdates is { Enabled: true, SilentInstallEnabled: true };

    private bool IsSuppressed(UpdateInfo info, bool respectSnooze)
    {
        var autoUpdates = _settings?.Current.AutoUpdates;
        if (autoUpdates == null) return false;
        if (respectSnooze && autoUpdates.SnoozedUntilUtc is DateTime until && until > DateTime.UtcNow)
            return true;
        string latest = NormalizeVersion(info.LatestVersion);
        string skipped = NormalizeVersion(autoUpdates.SkippedVersion);
        return latest.Length > 0 && skipped.Length > 0 &&
               string.Equals(latest, skipped, StringComparison.OrdinalIgnoreCase);
    }

    private CancellationToken CurrentEpoch()
    {
        CancellationToken token = _lifecycle.Start();
        return token;
    }

    private static string NormalizeVersion(string? version)
        => string.IsNullOrWhiteSpace(version) ? "" : version.Trim().TrimStart('v', 'V');

    private static IDisposable CreateRecurringTimer(TimeSpan interval, Action callback)
        => new System.Threading.Timer(_ => callback(), null, interval, interval);
}
