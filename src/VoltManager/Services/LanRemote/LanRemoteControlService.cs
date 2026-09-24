using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

internal sealed record LanRemoteControlActions(
    Func<PowerPlan?> GetActivePlan,
    Func<PlanId, bool> SetManualPlan,
    Action<ScheduledPowerActionType> ExecutePowerAction,
    Func<string> GetVersion);

public sealed record LanRemoteEnableResult(LanRemoteControlState State, string? GeneratedPin);

public sealed class LanRemoteControlService : IDisposable
{
    internal const string SessionCookieName = "vm_lan_session";
    internal const string CsrfHeaderName = "X-CSRF-Token";

    private readonly SettingsService _settings;
    private readonly LanRemoteControlActions _actions;
    private readonly LanRemoteAuthStore _auth;
    private readonly LanRemoteSessionStore _sessions = new();
    private readonly LanRemoteLoginRateLimiter _rateLimiter = new();
    private readonly ILanRemoteFirewall _firewall;
    private readonly Func<IReadOnlyList<IPAddress>> _addressProvider;
    private readonly Func<IReadOnlyList<IPAddress>, int, bool> _portAvailable;
    private readonly Func<IReadOnlyCollection<IPAddress>, X509Certificate2> _certificateProvider;
    private readonly Func<IPAddress?, bool> _clientAddressAllowed;
    private readonly string _remoteAssetsPath;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, Channel<byte>> _eventClients = new();

    private WebApplication? _webApplication;
    private X509Certificate2? _certificate;
    private IReadOnlyList<IPAddress> _addresses = [];
    private string? _fingerprint;
    private int _networkRestartQueued;
    private bool _networkSubscribed;
    private bool _disposed;

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
                ResolveVersion))
    {
        powerRequests.ActivePlanChanged += OnActivePlanChanged;
    }

    internal LanRemoteControlService(
        SettingsService settings,
        LanRemoteControlActions actions,
        LanRemoteAuthStore? auth = null,
        ILanRemoteFirewall? firewall = null,
        Func<IReadOnlyList<IPAddress>>? addressProvider = null,
        Func<IReadOnlyList<IPAddress>, int, bool>? portAvailable = null,
        Func<IReadOnlyCollection<IPAddress>, X509Certificate2>? certificateProvider = null,
        Func<IPAddress?, bool>? clientAddressAllowed = null,
        string? remoteAssetsPath = null)
    {
        _settings = settings;
        _actions = actions;
        _auth = auth ?? new LanRemoteAuthStore();
        _firewall = firewall ?? new LanRemoteFirewall();
        _addressProvider = addressProvider ?? LanRemoteNetwork.GetEligibleAddresses;
        _portAvailable = portAvailable ?? CanBind;
        _certificateProvider = certificateProvider ?? LanRemoteCertificateManager.GetOrCreate;
        _clientAddressAllowed = clientAddressAllowed ?? (address => address is not null && LanRemoteNetwork.IsPrivateLanAddress(address));
        _remoteAssetsPath = remoteAssetsPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot", "remote");
    }

    public LanRemoteControlState GetState()
    {
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
        IReadOnlyList<string> addresses = _addresses.Select(static address => address.ToString()).ToArray();
        return new LanRemoteControlState
        {
            Enabled = settings.Enabled,
            Running = _webApplication is not null,
            Port = settings.Port,
            Addresses = addresses,
            Urls = addresses.Select(address => $"https://{address}:{settings.Port}/").ToArray(),
            TlsFingerprintSha256 = _fingerprint,
            HasPin = _auth.HasPin,
            AllowPlanChange = settings.AllowPlanChange,
            AllowShutdown = settings.AllowShutdown,
            AllowRestart = settings.AllowRestart,
        };
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (!_settings.Current.LanRemoteControl.Enabled)
            return;
        StartAsync().GetAwaiter().GetResult();
    }

    public async Task<LanRemoteEnableResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        string? generatedPin = null;
        if (enabled && !_auth.HasPin)
        {
            generatedPin = LanRemotePinAuth.GeneratePin();
            _auth.SetPin(generatedPin);
        }

        _settings.Update(state => state.LanRemoteControl.Enabled = enabled);
        if (enabled)
            await StartAsync(cancellationToken).ConfigureAwait(false);
        else
            await StopAsync(cancellationToken).ConfigureAwait(false);

        return new LanRemoteEnableResult(GetState(), generatedPin);
    }

    public LanRemoteControlState SetPermissions(bool allowPlanChange, bool allowShutdown, bool allowRestart)
    {
        ThrowIfDisposed();
        _settings.Update(state =>
        {
            state.LanRemoteControl.AllowPlanChange = allowPlanChange;
            state.LanRemoteControl.AllowShutdown = allowShutdown;
            state.LanRemoteControl.AllowRestart = allowRestart;
        });
        PublishStateChanged();
        return GetState();
    }

    public string GeneratePin()
    {
        ThrowIfDisposed();
        string pin = LanRemotePinAuth.GeneratePin();
        _auth.SetPin(pin);
        _sessions.Clear();
        PublishStateChanged();
        return pin;
    }

    public LanRemoteControlState SetPin(string pin)
    {
        ThrowIfDisposed();
        _auth.SetPin(pin);
        _sessions.Clear();
        PublishStateChanged();
        return GetState();
    }

    public void Stop()
        => StopAsync().GetAwaiter().GetResult();

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNetworkSubscription();
            if (!_settings.Current.LanRemoteControl.Enabled || _webApplication is not null)
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
            WebApplication app = BuildWebApplication(addresses, port, certificate);
            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                _firewall.Apply(port);
            }
            catch
            {
                try { await app.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                await app.DisposeAsync().ConfigureAwait(false);
                certificate.Dispose();
                TryRemoveFirewall();
                throw;
            }

            _addresses = addresses;
            _certificate = certificate;
            _fingerprint = LanRemoteCertificateManager.Sha256Fingerprint(certificate);
            _webApplication = app;
            PublishStateChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WebApplication? app = _webApplication;
            _webApplication = null;
            _sessions.Clear();
            CompleteEventClients();
            if (app is not null)
            {
                try { await app.StopAsync(cancellationToken).ConfigureAwait(false); }
                finally { await app.DisposeAsync().ConfigureAwait(false); }
            }

            _certificate?.Dispose();
            _certificate = null;
            _addresses = [];
            _fingerprint = null;
            TryRemoveFirewall();
            PublishStateChanged();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveNetworkSubscription();
        try { StopAsync().GetAwaiter().GetResult(); } catch (Exception ex) { Logger.Warn("LAN remote-control stop failed: " + ex.Message); }
        _lifecycleGate.Dispose();
    }

    private WebApplication BuildWebApplication(IReadOnlyList<IPAddress> addresses, int port, X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LanRemoteControlService).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            foreach (IPAddress address in addresses)
            {
                options.Listen(address, port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(certificate);
                });
            }
        });

        WebApplication app = builder.Build();
        string[] allowedHosts = [.. addresses.Select(static address => address.ToString()), Environment.MachineName];

        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { error = "internal_error" });
        }));

        app.Use(async (context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.XFrameOptions = "DENY";
            if (context.Request.Path.StartsWithSegments("/api"))
                context.Response.Headers.CacheControl = "no-store";

            if (!_clientAddressAllowed(context.Connection.RemoteIpAddress)
                || !LanRemoteOriginPolicy.IsAllowedHost(context.Request.Host.Value, port, allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
                return;
            }

            if (HttpMethods.IsPost(context.Request.Method)
                && !LanRemoteOriginPolicy.IsAllowed(
                    context.Request.Host.Value,
                    context.Request.Headers.Origin.ToString(),
                    port,
                    allowedHosts))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "origin_rejected" });
                return;
            }

            await next();
        });

        MapStaticAssets(app);
        MapApi(app);
        return app;
    }

    private void MapStaticAssets(WebApplication app)
    {
        if (Directory.Exists(_remoteAssetsPath))
        {
            var provider = new PhysicalFileProvider(_remoteAssetsPath);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapGet("/", context => SendAssetAsync(
                context,
                Path.Combine(_remoteAssetsPath, "index.html"),
                "text/html; charset=utf-8"));
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "vendor", "fonts");
        app.MapGet("/fonts/inter-latin.woff2", context => SendAssetAsync(context, Path.Combine(webRoot, "inter-latin.woff2"), "font/woff2"));
        app.MapGet("/fonts/material-symbols-outlined.woff2", context => SendAssetAsync(context, Path.Combine(webRoot, "material-symbols-outlined.woff2"), "font/woff2"));
    }

    private void MapApi(WebApplication app)
    {
        app.MapPost("/api/auth/login", async context =>
        {
            string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!_rateLimiter.TryAcquire(ip, DateTimeOffset.UtcNow))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.Response.WriteAsJsonAsync(new { error = "rate_limited" });
                return;
            }

            string? pin = null;
            try
            {
                JsonElement payload = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
                if (payload.ValueKind == JsonValueKind.Object
                    && payload.TryGetProperty("pin", out JsonElement pinElement)
                    && pinElement.ValueKind == JsonValueKind.String)
                {
                    pin = pinElement.GetString();
                }
            }
            catch (JsonException) { }

            if (!_auth.Verify(pin))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = "invalid_credentials" });
                return;
            }

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
            await context.Response.WriteAsJsonAsync(new { csrfToken = session.CsrfToken });
        });

        app.MapPost("/api/auth/logout", async context =>
        {
            if (!TryAuthorizePost(context, out string sessionId)) return;
            _sessions.Revoke(sessionId);
            context.Response.Cookies.Delete(SessionCookieName, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
            await context.Response.WriteAsJsonAsync(new { success = true });
        });

        app.MapGet("/api/state", async context =>
        {
            if (!TryAuthorize(context, out _)) return;
            await context.Response.WriteAsJsonAsync(BuildRemoteState());
        });

        app.MapGet("/api/events", HandleEventsAsync);

        app.MapPost("/api/actions/power-plan", async context =>
        {
            if (!TryAuthorizePost(context, out _)) return;
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
                await context.Response.WriteAsJsonAsync(new { error = "invalid_plan" });
                return;
            }

            if (!_actions.SetManualPlan(plan.Value))
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = "action_failed" });
                return;
            }

            PublishEvent();
            await context.Response.WriteAsJsonAsync(new { success = true });
        });

        app.MapPost("/api/actions/shutdown", context => ExecutePowerActionAsync(context, LanRemoteAction.Shutdown, ScheduledPowerActionType.Shutdown));
        app.MapPost("/api/actions/restart", context => ExecutePowerActionAsync(context, LanRemoteAction.Restart, ScheduledPowerActionType.Restart));
    }

    private async Task HandleEventsAsync(HttpContext context)
    {
        if (!TryAuthorize(context, out string sessionId)) return;
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
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (!context.RequestAborted.IsCancellationRequested)
            {
                Task<bool> eventWait = channel.Reader.WaitToReadAsync(context.RequestAborted).AsTask();
                Task<bool> heartbeatWait = heartbeat.WaitForNextTickAsync(context.RequestAborted).AsTask();
                Task completed = await Task.WhenAny(eventWait, heartbeatWait);
                if (!_sessions.TryValidate(sessionId, out _))
                    break;

                if (completed == eventWait && await eventWait)
                {
                    while (channel.Reader.TryRead(out _)) { }
                    await context.Response.WriteAsync("event: state\ndata: {}\n\n", context.RequestAborted);
                }
                else
                {
                    await context.Response.WriteAsync(": keepalive\n\n", context.RequestAborted);
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

    private async Task ExecutePowerActionAsync(HttpContext context, LanRemoteAction permission, ScheduledPowerActionType action)
    {
        if (!TryAuthorizePost(context, out _)) return;
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
        if (!LanRemotePermissionPolicy.Allows(permission, settings))
        {
            await WriteForbiddenAsync(context, "permission_denied");
            return;
        }

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        await context.Response.WriteAsJsonAsync(new { accepted = true });
        await context.Response.CompleteAsync();
        _actions.ExecutePowerAction(action);
    }

    private object BuildRemoteState()
    {
        LanRemoteControlSettings settings = _settings.Current.LanRemoteControl;
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
            },
        };
    }

    private bool TryAuthorize(HttpContext context, out string sessionId)
    {
        sessionId = context.Request.Cookies[SessionCookieName] ?? "";
        if (_sessions.TryValidate(sessionId, out _))
            return true;
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json";
        context.Response.WriteAsync("{\"error\":\"unauthorized\"}").GetAwaiter().GetResult();
        return false;
    }

    private bool TryAuthorizePost(HttpContext context, out string sessionId)
    {
        if (!TryAuthorize(context, out sessionId))
            return false;
        string csrf = context.Request.Headers[CsrfHeaderName].ToString();
        if (_sessions.ValidateCsrf(sessionId, csrf))
            return true;
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        context.Response.WriteAsync("{\"error\":\"csrf_rejected\"}").GetAwaiter().GetResult();
        return false;
    }

    private static async Task<string?> ReadStringPropertyAsync(HttpContext context, string property)
    {
        try
        {
            JsonElement payload = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static async Task WriteForbiddenAsync(HttpContext context, string code)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = code });
    }

    private static async Task SendAssetAsync(HttpContext context, string path, string contentType)
    {
        if (!File.Exists(path))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.ContentType = contentType;
        await context.Response.SendFileAsync(path);
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
                if (_settings.Current.LanRemoteControl.Enabled && !_disposed)
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
        if (_disposed) throw new ObjectDisposedException(nameof(LanRemoteControlService));
    }
}
