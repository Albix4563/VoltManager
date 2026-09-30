
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VoltManager.Models;
using VoltManager.Services;
using VoltManager.Services.LanRemote;

namespace VoltManager.Tests;

public sealed class LanRemoteControlIntegrationTests
{
    [Fact]
    public void DisabledService_DoesNotCreateListenerOrApplyFirewallRule()
    {
        SettingsService settings = TestSettings.Create();
        var firewall = new RecordingFirewall();
        int addressReads = 0;
        using var service = new LanRemoteControlService(
            settings,
            CreateActions(),
            auth: CreateAuthStore(out _),
            firewall: firewall,
            addressProvider: () =>
            {
                addressReads++;
                return [IPAddress.Loopback];
            },
            clientAddressAllowed: _ => true);

        service.Start();

        Assert.False(service.GetState().Running);
        Assert.Equal(0, addressReads);
        Assert.Equal(0, firewall.ApplyCount);
    }

    [Fact]
    public async Task Api_LoginProtectsStateRequiresCsrfAndLogoutRevokesSession()
    {
        const string pin = "Abc2345678";
        int port = GetFreeTcpPort();
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        string remoteAssetsPath = Path.Combine(root, "remote-assets");
        Directory.CreateDirectory(remoteAssetsPath);
        await File.WriteAllTextAsync(
            Path.Combine(remoteAssetsPath, "index.html"),
            "<!doctype html><html><body>VoltManager Remote</body></html>");
        var auth = new LanRemoteAuthStore(Path.Combine(root, "remote-control-auth.json"));
        auth.SetPin(pin);
        settings.Update(current =>
        {
            current.LanRemoteControl.Enabled = true;
            current.LanRemoteControl.Port = port;
            current.LanRemoteControl.AllowPlanChange = true;
        });

        var firewall = new RecordingFirewall();
        var audit = new List<string>();
        using var service = new LanRemoteControlService(
            settings,
            CreateActions(),
            auth: auth,
            firewall: firewall,
            addressProvider: () => [IPAddress.Loopback],
            portAvailable: (_, _) => true,
            clientAddressAllowed: _ => true,
            remoteAssetsPath: remoteAssetsPath,
            useHttps: false,
            auditLogger: audit.Add);

        await service.StartAsync();
        Assert.True(service.GetState().Running);
        Assert.Equal(1, firewall.ApplyCount);

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        HttpResponseMessage remoteUi = await client.GetAsync("");
        Assert.Equal(HttpStatusCode.OK, remoteUi.StatusCode);
        Assert.Equal("text/html", remoteUi.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", remoteUi.Content.Headers.ContentType?.CharSet);
        Assert.Contains("<!doctype html>", await remoteUi.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage anonymous = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var oversizedLogin = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = new StringContent(
                "{\"pin\":\"" + new string('A', 5000) + "\"}",
                Encoding.UTF8,
                "application/json")
        };
        AddOrigin(oversizedLogin, port);
        using HttpResponseMessage oversizedResponse = await client.SendAsync(oversizedLogin);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode);

        using var wrongLogin = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new { pin = "Wrong1234" })
        };
        AddOrigin(wrongLogin, port);
        using HttpResponseMessage wrongLoginResponse = await client.SendAsync(wrongLogin);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongLoginResponse.StatusCode);

        using var login = new HttpRequestMessage(HttpMethod.Post, "api/auth/login")
        {
            Content = JsonContent.Create(new { pin })
        };
        AddOrigin(login, port);
        HttpResponseMessage loginResponse = await client.SendAsync(login);
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        using JsonDocument loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        string csrf = loginJson.RootElement.GetProperty("csrfToken").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(csrf));
        Assert.Contains(audit, line => line.Contains("result=failure", StringComparison.Ordinal));
        Assert.Contains(audit, line => line.Contains("result=success", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, line => line.Contains(pin, StringComparison.Ordinal));

        HttpResponseMessage stateResponse = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        using JsonDocument stateJson = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
        Assert.True(stateJson.RootElement.GetProperty("permissions").GetProperty("planChange").GetBoolean());

        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, "api/actions/power-plan")
        {
            Content = JsonContent.Create(new { plan = "balanced" })
        };
        AddOrigin(noCsrf, port);
        HttpResponseMessage noCsrfResponse = await client.SendAsync(noCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrfResponse.StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "api/auth/logout");
        AddOrigin(logout, port);
        logout.Headers.Add(LanRemoteControlService.CsrfHeaderName, csrf);
        HttpResponseMessage logoutResponse = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        HttpResponseMessage afterLogout = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);

        await service.StopAsync();
        Assert.False(service.GetState().Running);
        Assert.True(firewall.RemoveCount >= 1);
    }

    [Fact]
    public async Task SharedStyles_AreAnonymousAndOtherMainAppAssetsRemainUnserved()
    {
        const string pin = "Abc2345678";
        int port = GetFreeTcpPort();
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        string remoteAssetsPath = Path.Combine(root, "remote-assets");
        Directory.CreateDirectory(remoteAssetsPath);
        await File.WriteAllTextAsync(
            Path.Combine(remoteAssetsPath, "index.html"),
            "<!doctype html><html><body>VoltManager Remote</body></html>");
        var auth = new LanRemoteAuthStore(Path.Combine(root, "remote-control-auth.json"));
        auth.SetPin(pin);
        settings.Update(current =>
        {
            current.LanRemoteControl.Enabled = true;
            current.LanRemoteControl.Port = port;
        });

        using var service = new LanRemoteControlService(
            settings,
            CreateActions(),
            auth: auth,
            firewall: new RecordingFirewall(),
            addressProvider: () => [IPAddress.Loopback],
            portAvailable: (_, _) => true,
            clientAddressAllowed: _ => true,
            remoteAssetsPath: remoteAssetsPath,
            useHttps: false);

        await service.StartAsync();
        using var handler = new HttpClientHandler { UseProxy = false };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        using HttpResponseMessage layers = await client.GetAsync("css/layers.css");
        Assert.Equal(HttpStatusCode.OK, layers.StatusCode);
        Assert.Equal("text/css", layers.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", layers.Content.Headers.ContentType?.CharSet);
        Assert.Contains(
            "@layer reset, tokens, base, layout, components, themes, effects, utilities, overrides;",
            await layers.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        AssertSecurityHeaders(layers);

        using HttpResponseMessage tokens = await client.GetAsync("css/tokens.css");
        Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
        Assert.Equal("text/css", tokens.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", tokens.Content.Headers.ContentType?.CharSet);
        string tokenBody = await tokens.Content.ReadAsStringAsync();
        Assert.Contains("@layer tokens {", tokenBody, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"blue\"]", tokenBody, StringComparison.Ordinal);
        AssertSecurityHeaders(tokens);

        foreach (string path in new[]
                 {
                     "css/unknown.css",
                     "css/app.css",
                     "js/app.js",
                     "css/%2e%2e/theme-colors.css",
                 })
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using HttpResponseMessage anonymousState = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousState.StatusCode);

        await service.StopAsync();
    }

    [Fact]
    public async Task PowerActions_RequireCsrfPermissionAndCurrentCapabilityBeforeRouting()
    {
        const string pin = "Abc2345678";
        int port = GetFreeTcpPort();
        SettingsService settings = TestSettings.Create(out string settingsPath);
        string root = Path.GetDirectoryName(settingsPath)!;
        var auth = new LanRemoteAuthStore(Path.Combine(root, "remote-control-auth.json"));
        auth.SetPin(pin);
        settings.Update(current =>
        {
            current.LanRemoteControl.Enabled = true;
            current.LanRemoteControl.Port = port;
            current.LanRemoteControl.AllowShutdown = true;
            current.LanRemoteControl.AllowRestart = true;
            current.LanRemoteControl.AllowSleep = true;
            current.LanRemoteControl.AllowHibernate = false;
        });

        WindowsPowerCapabilityState capabilities = new(SleepAvailable: true, HibernateAvailable: true);
        var routedActions = new List<ScheduledPowerActionType>();
        var delayedActions = new List<(ScheduledPowerActionType Action, int DelaySeconds)>();
        var audit = new List<string>();
        using var service = new LanRemoteControlService(
            settings,
            CreateActions(
                () => capabilities,
                routedActions.Add,
                (action, delay) => delayedActions.Add((action, delay))),
            auth: auth,
            firewall: new RecordingFirewall(),
            addressProvider: () => [IPAddress.Loopback],
            portAvailable: (_, _) => true,
            clientAddressAllowed: _ => true,
            remoteAssetsPath: root,
            useHttps: false,
            auditLogger: audit.Add);

        await service.StartAsync();
        using var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };

        using var login = new HttpRequestMessage(HttpMethod.Post, "api/auth/login") { Content = JsonContent.Create(new { pin }) };
        AddOrigin(login, port);
        using HttpResponseMessage loginResponse = await client.SendAsync(login);
        using JsonDocument loginJson = JsonDocument.Parse(await loginResponse.Content.ReadAsStringAsync());
        string csrf = loginJson.RootElement.GetProperty("csrfToken").GetString()!;

        using HttpResponseMessage stateResponse = await client.GetAsync("api/state");
        using JsonDocument stateJson = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
        Assert.True(stateJson.RootElement.GetProperty("capabilities").GetProperty("sleep").GetBoolean());
        Assert.True(stateJson.RootElement.GetProperty("capabilities").GetProperty("hibernate").GetBoolean());

        using HttpResponseMessage noCsrf = await SendPowerActionAsync(client, port, "sleep", csrf: null);
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
        Assert.Empty(routedActions);

        using HttpResponseMessage denied = await SendPowerActionAsync(client, port, "hibernate", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(routedActions);

        settings.Update(current => current.LanRemoteControl.AllowHibernate = true);
        capabilities = new WindowsPowerCapabilityState(SleepAvailable: true, HibernateAvailable: false);
        using HttpResponseMessage unavailable = await SendPowerActionAsync(client, port, "hibernate", csrf);
        Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
        Assert.Contains("capability_unavailable", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(routedActions);

        capabilities = new WindowsPowerCapabilityState(SleepAvailable: true, HibernateAvailable: true);
        using HttpResponseMessage hibernate = await SendPowerActionAsync(client, port, "hibernate", csrf);
        Assert.Equal(HttpStatusCode.Accepted, hibernate.StatusCode);
        Assert.Equal([ScheduledPowerActionType.Hibernate], routedActions);

        using HttpResponseMessage sleep = await SendPowerActionAsync(client, port, "sleep", csrf);
        Assert.Equal(HttpStatusCode.Accepted, sleep.StatusCode);
        Assert.Equal([ScheduledPowerActionType.Hibernate, ScheduledPowerActionType.Sleep], routedActions);

        using HttpResponseMessage shutdown = await SendPowerActionAsync(client, port, "shutdown", csrf);
        Assert.Equal(HttpStatusCode.Accepted, shutdown.StatusCode);
        using JsonDocument shutdownJson = JsonDocument.Parse(await shutdown.Content.ReadAsStringAsync());
        Assert.Equal(LanRemoteControlService.PowerActionDelaySeconds, shutdownJson.RootElement.GetProperty("delaySeconds").GetInt32());
        Assert.Equal(
            [(ScheduledPowerActionType.Shutdown, LanRemoteControlService.PowerActionDelaySeconds)],
            delayedActions);
        Assert.Contains(audit, line => line.Contains("action=Shutdown", StringComparison.Ordinal)
            && line.Contains("result=accepted", StringComparison.Ordinal));

        using HttpResponseMessage restart = await SendPowerActionAsync(client, port, "restart", csrf);
        Assert.Equal(HttpStatusCode.Accepted, restart.StatusCode);
        using JsonDocument restartJson = JsonDocument.Parse(await restart.Content.ReadAsStringAsync());
        Assert.Equal(LanRemoteControlService.PowerActionDelaySeconds, restartJson.RootElement.GetProperty("delaySeconds").GetInt32());
        Assert.Equal(
            [
                (ScheduledPowerActionType.Shutdown, LanRemoteControlService.PowerActionDelaySeconds),
                (ScheduledPowerActionType.Restart, LanRemoteControlService.PowerActionDelaySeconds),
            ],
            delayedActions);
        Assert.DoesNotContain(audit, line => line.Contains(pin, StringComparison.Ordinal));
    }

    private static LanRemoteControlActions CreateActions(
        Func<WindowsPowerCapabilityState>? getCapabilities = null,
        Action<ScheduledPowerActionType>? executePowerAction = null,
        Action<ScheduledPowerActionType, int>? executePowerActionWithDelay = null)
        => new(
            () => new PowerPlan { PlanId = PlanId.Balanced, Name = "Balanced", IsActive = true },
            _ => true,
            executePowerAction ?? (_ => { }),
            getCapabilities ?? (() => new WindowsPowerCapabilityState(SleepAvailable: true, HibernateAvailable: true)),
            () => "1.0.0",
            executePowerActionWithDelay);

    private static async Task<HttpResponseMessage> SendPowerActionAsync(
        HttpClient client,
        int port,
        string action,
        string? csrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/actions/{action}");
        AddOrigin(request, port);
        if (!string.IsNullOrEmpty(csrf))
            request.Headers.Add(LanRemoteControlService.CsrfHeaderName, csrf);
        return await client.SendAsync(request);
    }

    private static LanRemoteAuthStore CreateAuthStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        return new LanRemoteAuthStore(Path.Combine(root, "remote-control-auth.json"));
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void AddOrigin(HttpRequestMessage request, int port)
        => request.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{port}");

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains(
            "default-src 'self'",
            response.Headers.GetValues("Content-Security-Policy").Single(),
            StringComparison.Ordinal);
    }

    private sealed class RecordingFirewall : ILanRemoteFirewall
    {
        public int ApplyCount { get; private set; }
        public int RemoveCount { get; private set; }
        public void Apply(int port) => ApplyCount++;
        public void Remove() => RemoveCount++;
    }
}
