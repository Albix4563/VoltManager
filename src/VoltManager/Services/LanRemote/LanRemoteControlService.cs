using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

internal sealed record LanRemoteControlActions(
    Func<PowerPlan?> GetActivePlan,
    Func<PlanId, bool> SetManualPlan,
    Action<ScheduledPowerActionType> ExecutePowerAction,
    Func<WindowsPowerCapabilityState> GetPowerCapabilities,
    Func<string> GetVersion,
    Action<ScheduledPowerActionType, int>? ExecutePowerActionWithDelay = null);

public sealed record LanRemoteEnableResult(LanRemoteControlState State, string? GeneratedPin);

public sealed class LanRemoteControlService : IDisposable
{
    internal const string SessionCookieName = "vm_lan_session";
    internal const string CsrfHeaderName = "X-CSRF-Token";
    internal const int PowerActionDelaySeconds = 10;
    internal const long MaxAuthRequestBodySize = 4 * 1024;

    private readonly SettingsService _settings;
    private readonly LanRemoteControlActions _actions;
    private readonly PowerRequestCoordinator? _powerRequests;
    private readonly LanRemoteAuthStore _auth;
    private readonly LanRemoteSessionStore _sessions = new();
    private readonly LanRemoteLoginRateLimiter _rateLimiter = new();
    private readonly ILanRemoteFirewall _firewall;
    private readonly Func<IReadOnlyList<IPAddress>> _addressProvider;
    private readonly Func<IReadOnlyList<IPAddress>, int, bool> _portAvailable;
    private readonly Func<IReadOnlyCollection<IPAddress>, X509Certificate2> _certificateProvider;
    private readonly Func<IPAddress?, bool> _clientAddressAllowed;
    private readonly string _remoteAssetsPath;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Func<CancellationToken, Task> _beforeStart;
    private readonly Func<CancellationToken, Task> _beforeStop;
    private readonly Action<string> _warningLogger;
    private readonly Action<string> _auditLogger;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _enableGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<byte>> _eventClients = new();

    private LanRemoteWebHost? _webHost;
    private X509Certificate2? _certificate;
    private IReadOnlyList<IPAddress> _addresses = [];
    private string? _fingerprint;
    private int _networkRestartQueued;
    private long _lifecycleRequestVersion;
    private bool _networkSubscribed;
    private string? _lastError;
    private int _disposed;

    public LanRemoteControlService(
        SettingsService settings,
        PowerRequestCoordinator powerRequests,
        ScheduledPowerActionService scheduledPowerActions)
        : this(
            settings,
            new LanRemoteControlActions(
                () => powerRequests.ActivePlan,
                plan => powerRequests.SetManualOverride(plan, null, "lan_remote"),
                scheduledPowerActions.ExecuteNow,
                WindowsPowerCapabilities.Query,
                ResolveVersion,
                scheduledPowerActions.ExecuteNow),
            powerRequests: powerRequests)
    { }

    internal LanRemoteControlService(
        SettingsService settings,
        LanRemoteControlActions actions,
        LanRemoteAuthStore? auth = null,
        ILanRemoteFirewall? firewall = null,
        Func<IReadOnlyList<IPAddress>>? addressProvider = null,
        Func<IReadOnlyList<IPAddress>, int, bool>? portAvailable = null,
        Func<IReadOnlyCollection<IPAddress>, X509Certificate2>? certificateProvider = null,
        Func<IPAddress?, bool>? clientAddressAllowed = null,
        string? remoteAssetsPath = null,
        TimeSpan? shutdownTimeout = null,
        Func<CancellationToken, Task>? beforeStart = null,
        Func<CancellationToken, Task>? beforeStop = null,
        Action<string>? warningLogger = null,
        Action<string>? auditLogger = null,
        PowerRequestCoordinator? powerRequests = null)
    {
        _settings = settings;
        _actions = actions;
        _powerRequests = powerRequests;
        _auth = auth ?? new LanRemoteAuthStore();
        _firewall = firewall ?? new LanRemoteFirewall();
        _addressProvider = addressProvider ?? LanRemoteNetwork.GetEligibleAddresses;
        _portAvailable = portAvailable ?? CanBind;
        _certificateProvider = certificateProvider ?? LanRemoteCertificateManager.GetOrCreate;
        _clientAddressAllowed = clientAddressAllowed ?? (address => address is not null && LanRemoteNetwork.IsPrivateLanAddress(address));
        _remoteAssetsPath = remoteAssetsPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot", "remote");
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        _beforeStart = beforeStart ?? (_ => Task.CompletedTask);
        _beforeStop = beforeStop ?? (_ => Task.CompletedTask);
        _warningLogger = warningLogger ?? Logger.Warn;
        _auditLogger = auditLogger ?? Logger.Info;

        if (_auth.IsCorrupt)
        {
            if (_settings.Current.LanRemoteControl.Enabled)
                _settings.Update(state => state.LanRemoteControl.Enabled = false);
            _warningLogger("LAN remote-control authentication data is corrupt; remote control was disabled.");
        }

        if (_powerRequests is not null)
            _powerRequests.ActivePlanChanged += OnActivePlanChanged;
    }

    public LanRemoteControlState GetState()
    {
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
        WindowsPowerCapabilityState capabilities = _actions.GetPowerCapabilities();
        IReadOnlyList<string> addresses = _addresses.Select(static address => address.ToString()).ToArray();
        return new LanRemoteControlState
        {
            Enabled = settings.Enabled,
            Running = _webHost is not null,
            Port = settings.Port,
            Addresses = addresses,
            Urls = addresses.Select(address => $"https://{address}:{settings.Port}/").ToArray(),
            TlsFingerprintSha256 = _fingerprint,
            HasPin = _auth.HasPin,
            PinNeedsRegeneration = _auth.PinNeedsRegeneration,
            AuthStoreCorrupt = _auth.IsCorrupt,
            LastError = _lastError,
            AllowPlanChange = settings.AllowPlanChange,
            AllowShutdown = settings.AllowShutdown,
            AllowRestart = settings.AllowRestart,
            AllowSleep = settings.AllowSleep,
            AllowHibernate = settings.AllowHibernate,
            SleepAvailable = capabilities.SleepAvailable,
            HibernateAvailable = capabilities.HibernateAvailable,
        };
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (!_settings.Current.LanRemoteControl.Enabled)
            return;
        ObserveDetached(StartAsync(), "start");
    }

    public async Task<LanRemoteEnableResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        // Serialize enable/disable so the persisted Enabled flag always matches the
        // outcome of the last completed request.
        await _enableGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SetEnabledCoreAsync(enabled, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _enableGate.Release();
        }
    }

    private async Task<LanRemoteEnableResult> SetEnabledCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        string? generatedPin = null;
        if (!enabled)
        {
            _settings.Update(state => state.LanRemoteControl.Enabled = false);
            _lastError = null;
            await StopAsync(cancellationToken).ConfigureAwait(false);
            return new LanRemoteEnableResult(GetState(), null);
        }

        if (_auth.IsCorrupt)
        {
            _settings.Update(state => state.LanRemoteControl.Enabled = false);
            _lastError = "Authentication data is corrupt. Set a new access secret.";
            PublishStateChanged();
            return new LanRemoteEnableResult(GetState(), null);
        }

        if (!_auth.HasPin)
        {
            generatedPin = LanRemotePinAuth.GeneratePin();
            _auth.SetPin(generatedPin);
        }

        _lastError = null;
        try
        {
            await RequestStateAsync(running: true, cancellationToken, allowStartWhenDisabled: true).ConfigureAwait(false);
            if (_webHost is null)
                throw new InvalidOperationException("No eligible LAN address is available.");

            _settings.Update(state => state.LanRemoteControl.Enabled = true);
            PublishStateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _settings.Update(state => state.LanRemoteControl.Enabled = false);
            _lastError = ex.Message;
            Logger.Error("LAN remote-control start failed", ex);
            try { await StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* best-effort: cleanup after a failed start must not mask the start error. */ }
            PublishStateChanged();
        }

        return new LanRemoteEnableResult(GetState(), generatedPin);
    }

    public LanRemoteControlState SetPermissions(
        bool allowPlanChange,
        bool allowShutdown,
        bool allowRestart,
        bool allowSleep,
        bool allowHibernate)
    {
        ThrowIfDisposed();
        _settings.Update(state =>
        {
            state.LanRemoteControl.AllowPlanChange = allowPlanChange;
            state.LanRemoteControl.AllowShutdown = allowShutdown;
            state.LanRemoteControl.AllowRestart = allowRestart;
            state.LanRemoteControl.AllowSleep = allowSleep;
            state.LanRemoteControl.AllowHibernate = allowHibernate;
        });
        PublishStateChanged();
        return GetState();
    }

    public string GeneratePin()
    {
        ThrowIfDisposed();
        string pin = LanRemotePinAuth.GeneratePin();
        _auth.SetPin(pin);
        _lastError = null;
        _sessions.Clear();
        PublishStateChanged();
        return pin;
    }

    public LanRemoteControlState SetPin(string pin)
    {
        ThrowIfDisposed();
        _auth.SetPin(pin);
        _lastError = null;
        _sessions.Clear();
        PublishStateChanged();
        return GetState();
    }

    public void Stop()
    {
        ObserveDetached(StopAsync(), "stop");
    }

    internal Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_settings.Current.LanRemoteControl.Enabled)
            return Task.CompletedTask;
        return RequestStateAsync(running: true, cancellationToken);
    }

    internal Task StopAsync(CancellationToken cancellationToken = default)
        => RequestStateAsync(running: false, cancellationToken);

    private Task RequestStateAsync(bool running, CancellationToken cancellationToken, bool allowStartWhenDisabled = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        long requestVersion = Interlocked.Increment(ref _lifecycleRequestVersion);
        return Task.Run(() => ApplyStateAsync(requestVersion, running, allowStartWhenDisabled, cancellationToken), cancellationToken);
    }

    private async Task ApplyStateAsync(
        long requestVersion,
        bool running,
        bool allowStartWhenDisabled,
        CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requestVersion != Volatile.Read(ref _lifecycleRequestVersion))
                return;

            await (running ? _beforeStart(cancellationToken) : _beforeStop(cancellationToken)).ConfigureAwait(false);
            if (requestVersion != Volatile.Read(ref _lifecycleRequestVersion))
                return;

            if (running)
                await StartCoreAsync(cancellationToken, allowStartWhenDisabled).ConfigureAwait(false);
            else
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken, bool allowStartWhenDisabled = false)
    {
        EnsureNetworkSubscription();
        if ((!allowStartWhenDisabled && !_settings.Current.LanRemoteControl.Enabled) || _webHost is not null)
            return;

        IReadOnlyList<IPAddress> addresses = _addressProvider()
            .Distinct()
            .OrderBy(static address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (addresses.Count == 0)
        {
            _addresses = [];
            _fingerprint = null;
            TryRemoveFirewall();
            PublishStateChanged();
            return;
        }

        LanRemoteControlSettings config = _settings.Current.LanRemoteControl;
        int port = config.Port;
        if (!_portAvailable(addresses, port))
            port = LanRemotePortSelector.Select(51737, candidate => _portAvailable(addresses, candidate));
        if (port != config.Port)
            _settings.Update(state => state.LanRemoteControl.Port = port);

        X509Certificate2 certificate = _certificateProvider(addresses);
        LanRemoteWebHost? host = null;
        try
        {
            host = await LanRemoteWebHost.StartAsync(
                this,
                addresses,
                port,
                certificate,
                _remoteAssetsPath,
                _clientAddressAllowed,
                cancellationToken).ConfigureAwait(false);
            _firewall.Apply(port);
        }
        catch
        {
            try
            {
                if (host is not null)
                    await host.StopAndDisposeAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _warningLogger("Impossibile chiudere il controllo remoto LAN: " + ex.Message); }
            finally
            {
                certificate.Dispose();
                TryRemoveFirewall();
            }
            throw;
        }

        _addresses = addresses;
        _certificate = certificate;
        _fingerprint = LanRemoteCertificateManager.Sha256Fingerprint(certificate);
        _webHost = host;
        PublishStateChanged();
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        LanRemoteWebHost? host = _webHost;
        _webHost = null;
        _sessions.Clear();
        CompleteEventClients();
        if (host is not null)
        {
            Task cleanup = host.StopAndDisposeAsync(cancellationToken);
            try
            {
                await cleanup.WaitAsync(_shutdownTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _warningLogger($"LAN remote-control cleanup did not complete within {_shutdownTimeout.TotalSeconds:F1}s; cleanup continues in the background.");
                ObserveDetached(cleanup, "cleanup");
            }
        }

        _certificate?.Dispose();
        _certificate = null;
        _addresses = [];
        _fingerprint = null;
        TryRemoveFirewall();
        PublishStateChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_powerRequests is not null)
            _powerRequests.ActivePlanChanged -= OnActivePlanChanged;
        RemoveNetworkSubscription();
        Stop();
    }

    private static void ObserveDetached(Task task, string operation)
    {
        _ = task.ContinueWith(
            completed => Logger.Error("LAN remote-control " + operation + " failed", completed.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal async Task HandleLoginAsync(HttpContext context)
    {
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        bool hasValidSession = HasValidSession(context);
        LanRemoteRateLimitResult limit = _rateLimiter.TryAcquire(ip, hasValidSession);
        if (!limit.Allowed)
        {
            int retrySeconds = Math.Max(1, (int)Math.Ceiling(limit.RetryAfter.TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = retrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(new { error = "rate_limited" }, context.RequestAborted);
            AuditLogin(ip, "locked");
            return;
        }

        (string? pin, bool tooLarge) = await ReadLoginPinAsync(context);
        if (tooLarge)
        {
            _rateLimiter.RecordFailure(ip);
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            context.Response.Headers.Connection = "close";
            await context.Response.WriteAsJsonAsync(new { error = "payload_too_large" }, context.RequestAborted);
            AuditLogin(ip, "payload_too_large");
            return;
        }

        if (!_auth.Verify(pin))
        {
            _rateLimiter.RecordFailure(ip);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_credentials" }, context.RequestAborted);
            AuditLogin(ip, "failure");
            return;
        }

        _rateLimiter.RecordSuccess(ip);
        LanRemoteSession session = _sessions.Create();
        context.Response.Cookies.Append(SessionCookieName, session.Id, new CookieOptions
        {
            Secure = true,
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = TimeSpan.FromHours(8),
            IsEssential = true,
        });
        await context.Response.WriteAsJsonAsync(new { csrfToken = session.CsrfToken }, context.RequestAborted);
        AuditLogin(ip, "success");
    }

    internal async Task HandleLogoutAsync(HttpContext context)
    {
        (bool authorized, string sessionId) = await TryAuthorizePostAsync(context);
        if (!authorized) return;
        _sessions.Revoke(sessionId);
        context.Response.Cookies.Delete(SessionCookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
        await context.Response.WriteAsJsonAsync(new { success = true }, context.RequestAborted);
    }

    internal async Task HandleGetStateAsync(HttpContext context)
    {
        var (authorized, _) = await TryAuthorizeAsync(context);
        if (!authorized) return;
        await context.Response.WriteAsJsonAsync(BuildRemoteState(), context.RequestAborted);
    }

    internal async Task HandlePowerPlanAsync(HttpContext context)
    {
        var (authorized, _) = await TryAuthorizePostAsync(context);
        if (!authorized) return;
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
        if (!LanRemotePermissionPolicy.Allows(LanRemoteAction.PowerPlan, settings))
        {
            await WriteForbiddenAsync(context, "permission_denied");
            return;
        }

        string? planValue = await ReadStringPropertyAsync(context, "plan");
        PlanId? plan = planValue switch
        {
            "powerSaver" => PlanId.PowerSaver,
            "balanced" => PlanId.Balanced,
            "performance" => PlanId.Performance,
            _ => null,
        };
        if (plan is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "invalid_plan" }, context.RequestAborted);
            return;
        }

        if (!_actions.SetManualPlan(plan.Value))
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new { error = "action_failed" }, context.RequestAborted);
            return;
        }

        PublishEvent();
        await context.Response.WriteAsJsonAsync(new { success = true }, context.RequestAborted);
    }

    internal async Task HandleEventsAsync(HttpContext context)
    {
        (bool authorized, string sessionId) = await TryAuthorizeAsync(context);
        if (!authorized) return;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Connection = "keep-alive";

        Guid id = Guid.NewGuid();
        Channel<byte> channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _eventClients[id] = channel;
        try
        {
            await context.Response.WriteAsync("event: ready\ndata: {}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            Task<bool> eventWait = channel.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
            Task heartbeatWait = Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
            while (!context.RequestAborted.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(eventWait, heartbeatWait);
                if (!_sessions.TryValidate(sessionId, out _))
                    break;

                if (completed == eventWait)
                {
                    if (!await eventWait)
                        break;
                    while (channel.Reader.TryRead(out _)) { }
                    await context.Response.WriteAsync("event: state\ndata: {}\n\n", context.RequestAborted);
                    eventWait = channel.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                }
                else
                {
                    await context.Response.WriteAsync(": keepalive\n\n", context.RequestAborted);
                    heartbeatWait = Task.Delay(TimeSpan.FromSeconds(15), context.RequestAborted);
                }
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        finally
        {
            _eventClients.TryRemove(id, out _);
        }
    }

    internal async Task ExecutePowerActionAsync(HttpContext context, LanRemoteAction permission, ScheduledPowerActionType action)
    {
        string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        bool authorized;
        try
        {
            (authorized, _) = await TryAuthorizePostAsync(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            AuditPowerAction(ip, action, "authorization_rejected");
            throw;
        }
        if (!authorized)
        {
            AuditPowerAction(ip, action, "authorization_rejected");
            return;
        }
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
        if (!LanRemotePermissionPolicy.Allows(permission, settings))
        {
            AuditPowerAction(ip, action, "permission_denied");
            await WriteForbiddenAsync(context, "permission_denied");
            return;
        }

        WindowsPowerCapabilityState capabilities = _actions.GetPowerCapabilities();
        bool available = action switch
        {
            ScheduledPowerActionType.Sleep => capabilities.SleepAvailable,
            ScheduledPowerActionType.Hibernate => capabilities.HibernateAvailable,
            ScheduledPowerActionType.Shutdown or ScheduledPowerActionType.Restart => true,
            _ => false,
        };
        if (!available)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            AuditPowerAction(ip, action, "capability_unavailable");
            await context.Response.WriteAsJsonAsync(new { error = "capability_unavailable" }, context.RequestAborted);
            return;
        }

        int delaySeconds = action is ScheduledPowerActionType.Shutdown or ScheduledPowerActionType.Restart
            ? PowerActionDelaySeconds
            : 0;
        context.Response.StatusCode = StatusCodes.Status202Accepted;
        await context.Response.WriteAsJsonAsync(new { accepted = true, delaySeconds }, context.RequestAborted);
        await context.Response.CompleteAsync();
        try
        {
            if (delaySeconds > 0 && _actions.ExecutePowerActionWithDelay is not null)
                _actions.ExecutePowerActionWithDelay(action, delaySeconds);
            else
                _actions.ExecutePowerAction(action);
            AuditPowerAction(ip, action, "accepted");
        }
        catch (Exception ex)
        {
            AuditPowerAction(ip, action, "failed");
            Logger.Error($"LAN remote-control power action failed: {action}", ex);
        }
    }

    private object BuildRemoteState()
    {
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
        WindowsPowerCapabilityState capabilities = _actions.GetPowerCapabilities();
        PowerPlan? active = _actions.GetActivePlan();
        return new
        {
            device = Environment.MachineName,
            version = _actions.GetVersion(),
            plan = ToPlanKey(active?.PlanId),
            permissions = new
            {
                planChange = settings.AllowPlanChange,
                shutdown = settings.AllowShutdown,
                restart = settings.AllowRestart,
                sleep = settings.AllowSleep,
                hibernate = settings.AllowHibernate,
            },
            capabilities = new
            {
                sleep = capabilities.SleepAvailable,
                hibernate = capabilities.HibernateAvailable,
            },
        };
    }

    private bool HasValidSession(HttpContext context)
    {
        string sessionId = context.Request.Cookies[SessionCookieName] ?? "";
        return _sessions.TryValidate(sessionId, out _);
    }

    private async Task<(bool Authorized, string SessionId)> TryAuthorizeAsync(HttpContext context)
    {
        string sessionId = context.Request.Cookies[SessionCookieName] ?? "";
        if (_sessions.TryValidate(sessionId, out _))
            return (true, sessionId);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"unauthorized\"}", context.RequestAborted);
        return (false, sessionId);
    }

    private async Task<(bool Authorized, string SessionId)> TryAuthorizePostAsync(HttpContext context)
    {
        (bool authorized, string sessionId) = await TryAuthorizeAsync(context);
        if (!authorized)
            return (false, sessionId);
        string csrf = context.Request.Headers[CsrfHeaderName].ToString();
        if (_sessions.ValidateCsrf(sessionId, csrf))
            return (true, sessionId);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"csrf_rejected\"}", context.RequestAborted);
        return (false, sessionId);
    }

    private static async Task<(string? Pin, bool TooLarge)> ReadLoginPinAsync(HttpContext context)
    {
        using var payload = new MemoryStream();
        byte[] buffer = new byte[1024];
        try
        {
            while (true)
            {
                int remaining = checked((int)(MaxAuthRequestBodySize + 1 - payload.Length));
                int read = await context.Request.Body.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    context.RequestAborted);
                if (read == 0)
                    break;
                await payload.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
                if (payload.Length > MaxAuthRequestBodySize)
                    return (null, true);
            }
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
            when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, true);
        }

        try
        {
            JsonElement json = JsonSerializer.Deserialize<JsonElement>(payload.ToArray());
            if (json.ValueKind == JsonValueKind.Object
                && json.TryGetProperty("pin", out JsonElement pinElement)
                && pinElement.ValueKind == JsonValueKind.String)
            {
                return (pinElement.GetString(), false);
            }
        }
        catch (JsonException) { /* malformed JSON is treated as a missing value. */ }
        return (null, false);
    }

    internal void AuditLogin(string ip, string result)
        => _auditLogger($"LAN remote login ip={ip} result={result}");

    private void AuditPowerAction(string ip, ScheduledPowerActionType action, string result)
        => _auditLogger($"LAN remote power action ip={ip} action={action} result={result}");

    private static async Task<string?> ReadStringPropertyAsync(HttpContext context, string property)
    {
        try
        {
            JsonElement payload = await JsonSerializer.DeserializeAsync<JsonElement>(
                context.Request.Body,
                cancellationToken: context.RequestAborted);
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException) { /* malformed JSON is treated as a missing value. */ }
        return null;
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = code }, context.RequestAborted);
    }

    internal static async Task SendAssetAsync(HttpContext context, string path, string contentType)
    {
        if (!File.Exists(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = contentType;
        await context.Response.SendFileAsync(path, context.RequestAborted);
    }

    private static bool CanBind(IReadOnlyList<IPAddress> addresses, int port)
    {
        var sockets = new List<Socket>();
        try
        {
            foreach (IPAddress address in addresses)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(socket);
                socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(address, port));
            }
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            foreach (Socket socket in sockets)
                socket.Dispose();
        }
    }

    private void EnsureNetworkSubscription()
    {
        if (_networkSubscribed) return;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        _networkSubscribed = true;
    }

    private void RemoveNetworkSubscription()
    {
        if (!_networkSubscribed) return;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _networkSubscribed = false;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (!_settings.Current.LanRemoteControl.Enabled || Interlocked.Exchange(ref _networkRestartQueued, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
                if (_settings.Current.LanRemoteControl.Enabled && Volatile.Read(ref _disposed) == 0)
                    await StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Error("LAN remote-control network rebind failed", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _networkRestartQueued, 0);
            }
        });
    }

    private void OnActivePlanChanged(PowerPlan? _)
        => PublishEvent();

    private void PublishStateChanged()
    {
        PublishEvent();
    }

    private void PublishEvent()
    {
        foreach (Channel<byte> channel in _eventClients.Values)
            channel.Writer.TryWrite(1);
    }

    private void CompleteEventClients()
    {
        foreach ((_, Channel<byte> channel) in _eventClients)
            channel.Writer.TryComplete();
        _eventClients.Clear();
    }

    private void TryRemoveFirewall()
    {
        try { _firewall.Remove(); }
        catch (Exception ex) { Logger.Warn("LAN remote-control firewall cleanup failed: " + ex.Message); }
    }

    private static string? ToPlanKey(PlanId? plan)
        => plan switch
        {
            PlanId.PowerSaver => "powerSaver",
            PlanId.Balanced => "balanced",
            PlanId.Performance => "performance",
            _ => null,
        };

    private static string ResolveVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}
