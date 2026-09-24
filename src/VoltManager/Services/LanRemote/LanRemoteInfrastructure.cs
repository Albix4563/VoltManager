using System.Diagnostics;
using System.Formats.Asn1;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

internal enum LanRemoteAction
{
    PowerPlan,
    Shutdown,
    Restart,
}

internal static class LanRemotePermissionPolicy
{
    public static bool Allows(LanRemoteAction action, LanRemoteControlSettings settings)
        => action switch
        {
            LanRemoteAction.PowerPlan => settings.AllowPlanChange,
            LanRemoteAction.Shutdown => settings.AllowShutdown,
            LanRemoteAction.Restart => settings.AllowRestart,
            _ => false,
        };
}

internal static class LanRemoteOriginPolicy
{
    public static bool IsAllowed(string? hostHeader, string? originHeader, int port, IEnumerable<string> allowedHosts)
    {
        if (string.IsNullOrWhiteSpace(hostHeader) || string.IsNullOrWhiteSpace(originHeader))
            return false;

        if (!TrySplitAuthority(hostHeader, out string host, out int hostPort) || hostPort != port)
            return false;
        if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            return false;

        if (!Uri.TryCreate(originHeader, UriKind.Absolute, out Uri? origin)
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || origin.Port != port
            || !string.Equals(origin.Host, host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return allowedHosts.Contains(origin.Host, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAllowedHost(string? hostHeader, int port, IEnumerable<string> allowedHosts)
        => TrySplitAuthority(hostHeader, out string host, out int hostPort)
           && hostPort == port
           && allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private static bool TrySplitAuthority(string? authority, out string host, out int port)
    {
        host = "";
        port = -1;
        if (string.IsNullOrWhiteSpace(authority)) return false;
        if (!Uri.TryCreate("https://" + authority, UriKind.Absolute, out Uri? uri)) return false;
        host = uri.Host;
        port = uri.Port;
        return true;
    }
}

internal sealed record LanRemoteFirewallRule(
    string Name,
    string ApplicationName,
    int Protocol,
    string LocalPorts,
    int Profiles,
    string RemoteAddresses)
{
    public const string RuleName = "VoltManager LAN Remote Control";

    public static LanRemoteFirewallRule Create(int port, string applicationName)
        => new(RuleName, applicationName, 6, port.ToString(System.Globalization.CultureInfo.InvariantCulture), 2, "LocalSubnet");
}

internal interface ILanRemoteFirewall
{
    void Apply(int port);
    void Remove();
}

internal sealed class LanRemoteFirewall : ILanRemoteFirewall
{
    public void Apply(int port)
    {
        string executable = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not determine VoltManager executable path.");
        LanRemoteFirewallRule definition = LanRemoteFirewallRule.Create(port, Path.GetFullPath(executable));

        object? policy = null;
        object? rule = null;
        try
        {
            Type policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!;
            Type ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!;
            policy = Activator.CreateInstance(policyType)!;
            object rules = policyType.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null)!;
            try
            {
                rules.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, rules, [LanRemoteFirewallRule.RuleName]);
            }
            catch (COMException) { }

            rule = Activator.CreateInstance(ruleType)!;
            Set(ruleType, rule, "Name", definition.Name);
            Set(ruleType, rule, "Description", "VoltManager secure LAN remote control");
            Set(ruleType, rule, "ApplicationName", definition.ApplicationName);
            Set(ruleType, rule, "Protocol", definition.Protocol);
            Set(ruleType, rule, "LocalPorts", definition.LocalPorts);
            Set(ruleType, rule, "Direction", 1);
            Set(ruleType, rule, "Enabled", true);
            Set(ruleType, rule, "Profiles", definition.Profiles);
            Set(ruleType, rule, "Action", 1);
            Set(ruleType, rule, "RemoteAddresses", definition.RemoteAddresses);
            Set(ruleType, rule, "EdgeTraversal", false);
            rules.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, rules, [rule]);
            Release(rules);
        }
        finally
        {
            Release(rule);
            Release(policy);
        }
    }

    public void Remove()
    {
        object? policy = null;
        object? rules = null;
        try
        {
            Type? policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false);
            if (policyType is null) return;
            policy = Activator.CreateInstance(policyType);
            if (policy is null) return;
            rules = policyType.InvokeMember("Rules", BindingFlags.GetProperty, null, policy, null);
            if (rules is null) return;
            try
            {
                rules.GetType().InvokeMember("Remove", BindingFlags.InvokeMethod, null, rules, [LanRemoteFirewallRule.RuleName]);
            }
            catch (COMException) { }
        }
        finally
        {
            Release(rules);
            Release(policy);
        }
    }

    private static void Set(Type type, object target, string property, object value)
        => type.InvokeMember(property, BindingFlags.SetProperty, null, target, [value]);

    private static void Release(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}

internal static class LanRemoteCertificateManager
{
    private const string SubjectName = "CN=VoltManager LAN Remote";

    public static X509Certificate2 GetOrCreate(IReadOnlyCollection<IPAddress> addresses)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Certificate2? reusable = store.Certificates
            .OfType<X509Certificate2>()
            .Where(c => c.HasPrivateKey
                && string.Equals(c.Subject, SubjectName, StringComparison.OrdinalIgnoreCase)
                && c.NotAfter.ToUniversalTime() > now.AddDays(7).UtcDateTime)
            .FirstOrDefault(c => Covers(c, addresses, Environment.MachineName));
        if (reusable is not null)
            return new X509Certificate2(reusable);

        foreach (X509Certificate2 old in store.Certificates
                     .OfType<X509Certificate2>()
                     .Where(c => string.Equals(c.Subject, SubjectName, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            try { store.Remove(old); } catch { }
        }

        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        foreach (IPAddress address in addresses)
            san.AddIpAddress(address);
        request.CertificateExtensions.Add(san.Build());

        using X509Certificate2 generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(3));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
#pragma warning disable SYSLIB0057
        var persistent = new X509Certificate2(
            pfx,
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
        try { persistent.FriendlyName = "VoltManager LAN Remote"; } catch { }
        store.Add(persistent);
        return persistent;
    }

    public static string Sha256Fingerprint(X509Certificate2 certificate)
    {
        string hex = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool Covers(X509Certificate2 certificate, IReadOnlyCollection<IPAddress> addresses, string hostname)
    {
        X509Extension? extension = certificate.Extensions["2.5.29.17"];
        if (extension is null) return false;

        var dns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new HashSet<IPAddress>();
        try
        {
            var reader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
            AsnReader sequence = reader.ReadSequence();
            while (sequence.HasData)
            {
                Asn1Tag tag = sequence.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    dns.Add(sequence.ReadCharacterString(
                        UniversalTagNumber.IA5String,
                        new Asn1Tag(TagClass.ContextSpecific, 2)));
                }
                else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                {
                    ips.Add(new IPAddress(sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7))));
                }
                else
                {
                    sequence.ReadEncodedValue();
                }
            }
        }
        catch (AsnContentException)
        {
            return false;
        }

        return dns.Contains(hostname) && addresses.All(ips.Contains);
    }
}
