using System.Net;
using System.Net.NetworkInformation;
using System.IO;
using VoltManager.Models;
using VoltManager.Services.LanRemote;

namespace VoltManager.Tests;

public sealed class LanRemoteControlSecurityTests
{
    [Theory]
    [InlineData("Abc12345", true)]
    [InlineData("ABCDEFGH", true)]
    [InlineData("abc1234", false)]
    [InlineData("Abc1234!", false)]
    [InlineData(" Abc12345", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789AB", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABC", false)]
    public void PinValidation_RequiresEightToSixtyFourAsciiLettersOrDigits(string pin, bool expected)
        => Assert.Equal(expected, LanRemotePinAuth.IsValidPin(pin));

    [Fact]
    public void PinVerifier_UsesConfiguredPbkdf2ParametersAndRejectsWrongPin()
    {
        const string pin = "Abc12345";
        LanRemotePinVerifier verifier = LanRemotePinAuth.CreateVerifier(pin);

        Assert.Equal(600_000, verifier.Iterations);
        Assert.Equal(8, verifier.MinLength);
        Assert.Equal(64, verifier.MaxLength);
        Assert.Equal(0, verifier.Digits);
        Assert.Equal(16, Convert.FromBase64String(verifier.SaltBase64).Length);
        Assert.Equal(32, Convert.FromBase64String(verifier.HashBase64).Length);
        Assert.True(LanRemotePinAuth.Verify(pin, verifier));
        Assert.False(LanRemotePinAuth.Verify("Abc12346", verifier));
        Assert.False(LanRemotePinAuth.Verify("abc12345", verifier));
    }

    [Fact]
    public void GeneratedPin_IsTenUnambiguousAlphanumericCharacters()
    {
        for (int i = 0; i < 32; i++)
            Assert.Matches("^[A-HJ-NP-Za-km-z2-9]{10}$", LanRemotePinAuth.GeneratePin());
    }

    [Fact]
    public void Sessions_ExpireOnIdleAndAbsoluteTimeout_AndValidateCsrf()
    {
        DateTimeOffset now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var sessions = new LanRemoteSessionStore(() => now);
        LanRemoteSession session = sessions.Create();

        Assert.Equal(32, Convert.FromBase64String(session.Id).Length);
        Assert.Equal(32, Convert.FromBase64String(session.CsrfToken).Length);
        Assert.True(sessions.TryValidate(session.Id, out _));
        Assert.True(sessions.ValidateCsrf(session.Id, session.CsrfToken));
        Assert.False(sessions.ValidateCsrf(session.Id, session.CsrfToken + "x"));

        now = now.AddMinutes(29);
        Assert.True(sessions.TryValidate(session.Id, out _));
        now = now.AddMinutes(31);
        Assert.False(sessions.TryValidate(session.Id, out _));

        now = new DateTimeOffset(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        session = sessions.Create();
        for (int interval = 0; interval < 19; interval++)
        {
            now = now.AddMinutes(25);
            Assert.True(sessions.TryValidate(session.Id, out _));
        }
        now = now.AddMinutes(6);
        Assert.False(sessions.TryValidate(session.Id, out _));
    }

    [Fact]
    public void LoginRateLimiter_LocksPerIpEscalatesAndLeavesOtherIpsUnaffected()
    {
        DateTimeOffset now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var limiter = new LanRemoteLoginRateLimiter(() => now);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
            limiter.RecordFailure("192.168.1.10");
        }

        LanRemoteRateLimitResult firstLockout = limiter.TryAcquire("192.168.1.10");
        Assert.False(firstLockout.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(30), firstLockout.RetryAfter);
        Assert.True(limiter.TryAcquire("192.168.1.11").Allowed);

        now = now.AddSeconds(31);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
            limiter.RecordFailure("192.168.1.10");
        }

        LanRemoteRateLimitResult secondLockout = limiter.TryAcquire("192.168.1.10");
        Assert.False(secondLockout.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(60), secondLockout.RetryAfter);
    }

    [Fact]
    public void LoginRateLimiter_SuccessResetsIpPenalty()
    {
        DateTimeOffset now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var limiter = new LanRemoteLoginRateLimiter(() => now);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
            limiter.RecordFailure("192.168.1.10");
        }

        now = now.AddSeconds(31);
        Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
        limiter.RecordSuccess("192.168.1.10");

        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
            limiter.RecordFailure("192.168.1.10");
        }

        Assert.Equal(TimeSpan.FromSeconds(30), limiter.TryAcquire("192.168.1.10").RetryAfter);
    }

    [Fact]
    public void LoginRateLimiter_ValidSessionSkipsIpLockoutButNotGlobalCap()
    {
        DateTimeOffset now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var limiter = new LanRemoteLoginRateLimiter(() => now);

        for (int i = 0; i < LanRemoteLoginRateLimiter.FailureThreshold; i++)
        {
            Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
            limiter.RecordFailure("192.168.1.10");
        }
        Assert.False(limiter.TryAcquire("192.168.1.10").Allowed);
        Assert.True(limiter.TryAcquire("192.168.1.10", hasValidSession: true).Allowed);

        while (limiter.TryAcquire("192.168.1.99").Allowed) { }
        Assert.False(limiter.TryAcquire("192.168.1.10", hasValidSession: true).Allowed);
    }

    [Fact]
    public void LoginRateLimiter_PrunesExpiredIpEntries()
    {
        DateTimeOffset now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var limiter = new LanRemoteLoginRateLimiter(() => now);

        Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
        limiter.RecordFailure("192.168.1.10");
        Assert.Equal(1, limiter.TrackedIpCount);

        now = now.AddMinutes(16);
        Assert.True(limiter.TryAcquire("192.168.1.20").Allowed);
        Assert.Equal(0, limiter.TrackedIpCount);
    }

    [Fact]
    public void LoginRateLimiter_EscalationCapsAtFifteenMinutes()
    {
        DateTimeOffset now = new(2026, 9, 24, 8, 0, 0, TimeSpan.Zero);
        var limiter = new LanRemoteLoginRateLimiter(() => now);
        TimeSpan lastRetry = TimeSpan.Zero;

        for (int level = 0; level < 6; level++)
        {
            for (int attempt = 0; attempt < LanRemoteLoginRateLimiter.FailureThreshold; attempt++)
            {
                Assert.True(limiter.TryAcquire("192.168.1.10").Allowed);
                limiter.RecordFailure("192.168.1.10");
            }

            lastRetry = limiter.TryAcquire("192.168.1.10").RetryAfter;
            Assert.True(lastRetry <= TimeSpan.FromMinutes(15));
            now = now.Add(lastRetry).AddSeconds(1);
        }

        Assert.Equal(TimeSpan.FromMinutes(15), lastRetry);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.20", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    public void PrivateLanAddress_FilterMatchesRfc1918(string raw, bool expected)
        => Assert.Equal(expected, LanRemoteNetwork.IsPrivateLanAddress(IPAddress.Parse(raw)));

    [Fact]
    public void InterfaceFilter_OnlyAllowsActiveEthernetAndWifiPrivateIpv4()
    {
        Assert.True(LanRemoteNetwork.IsEligible(
            IPAddress.Parse("192.168.1.20"), NetworkInterfaceType.Ethernet, OperationalStatus.Up));
        Assert.True(LanRemoteNetwork.IsEligible(
            IPAddress.Parse("10.0.0.20"), NetworkInterfaceType.Wireless80211, OperationalStatus.Up));
        Assert.False(LanRemoteNetwork.IsEligible(
            IPAddress.Parse("192.168.1.20"), NetworkInterfaceType.Tunnel, OperationalStatus.Up));
        Assert.False(LanRemoteNetwork.IsEligible(
            IPAddress.Parse("192.168.1.20"), NetworkInterfaceType.Ethernet, OperationalStatus.Down));
        Assert.False(LanRemoteNetwork.IsEligible(
            IPAddress.Parse("169.254.10.20"), NetworkInterfaceType.Ethernet, OperationalStatus.Up));
    }

    [Fact]
    public void PortSelector_TriesConfiguredPortThenTenFallbackPorts()
    {
        var checkedPorts = new List<int>();
        int selected = LanRemotePortSelector.Select(51737, port =>
        {
            checkedPorts.Add(port);
            return port == 51741;
        });

        Assert.Equal(51741, selected);
        Assert.Equal([51737, 51738, 51739, 51740, 51741], checkedPorts);
        Assert.Throws<InvalidOperationException>(() => LanRemotePortSelector.Select(51737, _ => false));
    }

    [Fact]
    public void Settings_DefaultDisabledWithNoRemotePermissions()
    {
        var settings = new LanRemoteControlSettings();

        Assert.False(settings.Enabled);
        Assert.Equal(51737, settings.Port);
        Assert.False(settings.AllowPlanChange);
        Assert.False(settings.AllowShutdown);
        Assert.False(settings.AllowRestart);
        Assert.False(settings.AllowSleep);
        Assert.False(settings.AllowHibernate);
    }

    [Fact]
    public void AuthStore_FlagsLegacyFourDigitVerifierForRegeneration()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "remote-control-auth.json");
        try
        {
            Directory.CreateDirectory(root);
            var legacy = new LanRemotePinVerifier
            {
                SaltBase64 = Convert.ToBase64String(new byte[16]),
                HashBase64 = Convert.ToBase64String(new byte[32]),
                Iterations = LanRemotePinAuth.Iterations,
                Digits = 4,
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(legacy));

            var store = new LanRemoteAuthStore(path);
            Assert.True(store.HasPin);
            Assert.True(store.PinNeedsRegeneration);
            Assert.False(store.IsCorrupt);
            Assert.False(store.Verify("1234"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void AuthStore_MissingFileIsNotCorrupt()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "remote-control-auth.json");
        try
        {
            var store = new LanRemoteAuthStore(path);

            Assert.Equal(LanRemoteAuthStoreStatus.Missing, store.Status);
            Assert.False(store.HasPin);
            Assert.False(store.PinNeedsRegeneration);
            Assert.False(store.IsCorrupt);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void AuthStore_PersistsOnlyVerifierAndCanReloadIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "VoltManager.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "remote-control-auth.json");
        const string pin = "Abc98765";
        try
        {
            var store = new LanRemoteAuthStore(path);
            store.SetPin(pin);

            Assert.True(store.HasPin);
            Assert.True(store.Verify(pin));
            Assert.False(store.Verify("Abc98764"));
            string json = File.ReadAllText(path);
            Assert.DoesNotContain(pin, json, StringComparison.Ordinal);
            Assert.DoesNotContain("pin", json, StringComparison.OrdinalIgnoreCase);

            var reloaded = new LanRemoteAuthStore(path);
            Assert.True(reloaded.HasPin);
            Assert.False(reloaded.PinNeedsRegeneration);
            Assert.False(reloaded.IsCorrupt);
            Assert.True(reloaded.Verify(pin));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void FirewallRule_IsScopedToPrivateLocalSubnetTcpAndExecutable()
    {
        LanRemoteFirewallRule rule = LanRemoteFirewallRule.Create(51737, @"C:\Apps\VoltManager.exe");

        Assert.Equal(6, rule.Protocol);
        Assert.Equal(2, rule.Profiles);
        Assert.Equal("LocalSubnet", rule.RemoteAddresses);
        Assert.Equal("51737", rule.LocalPorts);
        Assert.Equal(@"C:\Apps\VoltManager.exe", rule.ApplicationName);
    }

    [Theory]
    [InlineData("192.168.1.20:51737", "https://192.168.1.20:51737", true)]
    [InlineData("volt-pc:51737", "https://volt-pc:51737", true)]
    [InlineData("192.168.1.20:51737", "http://192.168.1.20:51737", false)]
    [InlineData("192.168.1.20:51737", "https://192.168.1.21:51737", false)]
    [InlineData("other-host:51737", "https://other-host:51737", false)]
    public void OriginPolicy_RequiresAllowedHttpsSameOrigin(string host, string origin, bool expected)
    {
        string[] allowed = ["192.168.1.20", "volt-pc"];
        Assert.Equal(expected, LanRemoteOriginPolicy.IsAllowed(host, origin, 51737, allowed));
    }

    [Fact]
    public void PermissionPolicy_ChecksEveryActionServerSide()
    {
        var settings = new LanRemoteControlSettings
        {
            AllowPlanChange = true,
            AllowShutdown = false,
            AllowRestart = true,
            AllowSleep = true,
            AllowHibernate = false,
        };

        Assert.True(LanRemotePermissionPolicy.Allows(LanRemoteAction.PowerPlan, settings));
        Assert.False(LanRemotePermissionPolicy.Allows(LanRemoteAction.Shutdown, settings));
        Assert.True(LanRemotePermissionPolicy.Allows(LanRemoteAction.Restart, settings));
        Assert.True(LanRemotePermissionPolicy.Allows(LanRemoteAction.Sleep, settings));
        Assert.False(LanRemotePermissionPolicy.Allows(LanRemoteAction.Hibernate, settings));
    }
}
