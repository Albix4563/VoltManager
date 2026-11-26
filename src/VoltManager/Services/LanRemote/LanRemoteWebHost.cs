using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

/// <summary>
/// Isolates ASP.NET Core / Kestrel types from <see cref="LanRemoteControlService"/> so
/// the web-host assemblies are loaded by the CLR only when LAN Remote Control starts.
/// </summary>
internal sealed class LanRemoteWebHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LanRemoteWebHost(WebApplication app)
        => _app = app;

    public static async Task<LanRemoteWebHost> StartAsync(
        LanRemoteControlService service,
        IReadOnlyList<IPAddress> addresses,
        int port,
        X509Certificate2 certificate,
        string remoteAssetsPath,
        Func<IPAddress?, bool> clientAddressAllowed,
        CancellationToken cancellationToken)
    {
        WebApplication app = BuildWebApplication(
            service,
            addresses,
            port,
            certificate,
            remoteAssetsPath,
            clientAddressAllowed);
        var host = new LanRemoteWebHost(app);
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
            return host;
        }
        catch
        {
            await host.StopAndDisposeAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAndDisposeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
        => _app.DisposeAsync();

    private static WebApplication BuildWebApplication(
        LanRemoteControlService service,
        IReadOnlyList<IPAddress> addresses,
        int port,
        X509Certificate2 certificate,
        string remoteAssetsPath,
        Func<IPAddress?, bool> clientAddressAllowed)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(LanRemoteWebHost).Assembly.GetName().Name,
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

            if (context.Request.Path.StartsWithSegments("/api/auth"))
            {
                IHttpMaxRequestBodySizeFeature? bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySize is { IsReadOnly: false })
                    bodySize.MaxRequestBodySize = LanRemoteControlService.MaxAuthRequestBodySize;
                if (context.Request.ContentLength is long contentLength && contentLength > LanRemoteControlService.MaxAuthRequestBodySize)
                {
                    if (string.Equals(
                            context.Request.Path.Value,
                            "/api/auth/login",
                            StringComparison.OrdinalIgnoreCase))
                        service.AuditLogin(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", "payload_too_large");
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    context.Response.Headers.Connection = "close";
                    await context.Response.WriteAsJsonAsync(new { error = "payload_too_large" });
                    return;
                }
            }

            if (!clientAddressAllowed(context.Connection.RemoteIpAddress)
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

        MapStaticAssets(app, remoteAssetsPath);
        MapApi(app, service);
        return app;
    }

    private static void MapStaticAssets(WebApplication app, string remoteAssetsPath)
    {
        if (Directory.Exists(remoteAssetsPath))
        {
            var provider = new PhysicalFileProvider(remoteAssetsPath);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapGet("/", context => LanRemoteControlService.SendAssetAsync(
                context,
                Path.Combine(remoteAssetsPath, "index.html"),
                "text/html; charset=utf-8"));
        }

        string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "vendor", "fonts");
        app.MapGet("/fonts/inter-latin.woff2", context => LanRemoteControlService.SendAssetAsync(context, Path.Combine(webRoot, "inter-latin.woff2"), "font/woff2"));
        app.MapGet("/fonts/material-symbols-outlined.woff2", context => LanRemoteControlService.SendAssetAsync(context, Path.Combine(webRoot, "material-symbols-outlined.woff2"), "font/woff2"));
    }

    private static void MapApi(WebApplication app, LanRemoteControlService service)
    {
        app.MapPost("/api/auth/login", service.HandleLoginAsync);
        app.MapPost("/api/auth/logout", service.HandleLogoutAsync);
        app.MapGet("/api/state", service.HandleGetStateAsync);
        app.MapGet("/api/events", service.HandleEventsAsync);
        app.MapPost("/api/actions/power-plan", service.HandlePowerPlanAsync);
        app.MapPost("/api/actions/shutdown", context => service.ExecutePowerActionAsync(context, LanRemoteAction.Shutdown, ScheduledPowerActionType.Shutdown));
        app.MapPost("/api/actions/restart", context => service.ExecutePowerActionAsync(context, LanRemoteAction.Restart, ScheduledPowerActionType.Restart));
        app.MapPost("/api/actions/sleep", context => service.ExecutePowerActionAsync(context, LanRemoteAction.Sleep, ScheduledPowerActionType.Sleep));
        app.MapPost("/api/actions/hibernate", context => service.ExecutePowerActionAsync(context, LanRemoteAction.Hibernate, ScheduledPowerActionType.Hibernate));
    }
}
