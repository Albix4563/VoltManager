
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
    public async Task HttpsApi_LoginProtectsStateRequiresCsrfAndLogoutRevokesSession()
    {
        const string pin = "1234";
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
        using X509Certificate2 testCertificate = CreateTestCertificate();
        using var service = new LanRemoteControlService(
            settings,
            CreateActions(),
            auth: auth,
            firewall: firewall,
            addressProvider: () => [IPAddress.Loopback],
            portAvailable: (_, _) => true,
            certificateProvider: _ => testCertificate,
            clientAddressAllowed: _ => true,
            remoteAssetsPath: remoteAssetsPath);

        await service.StartAsync();
        Assert.True(service.GetState().Running);
        Assert.Equal(1, firewall.ApplyCount);

        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{port}/") };

        HttpResponseMessage remoteUi = await client.GetAsync("");
        Assert.Equal(HttpStatusCode.OK, remoteUi.StatusCode);
        Assert.Equal("text/html", remoteUi.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", remoteUi.Content.Headers.ContentType?.CharSet);
        Assert.Contains("<!doctype html>", await remoteUi.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        HttpResponseMessage anonymous = await client.GetAsync("api/state");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

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

    private static LanRemoteControlActions CreateActions()
        => new(
            () => new PowerPlan { PlanId = PlanId.Balanced, Name = "Balanced", IsActive = true },
            _ => true,
            _ => { },
            () => "1.0.0");

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

    private static X509Certificate2 CreateTestCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=VoltManager LAN Remote Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using X509Certificate2 generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
#pragma warning disable SYSLIB0057
        return new X509Certificate2(
            pfx,
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
    }

    private static void AddOrigin(HttpRequestMessage request, int port)
        => request.Headers.TryAddWithoutValidation("Origin", $"https://127.0.0.1:{port}");

    private sealed class RecordingFirewall : ILanRemoteFirewall
    {
        public int ApplyCount { get; private set; }
        public int RemoveCount { get; private set; }
        public void Apply(int port) => ApplyCount++;
        public void Remove() => RemoveCount++;
    }
}
