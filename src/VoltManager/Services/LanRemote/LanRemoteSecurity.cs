using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

internal static partial class LanRemotePinAuth
{
    public const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    [GeneratedRegex("^[0-9]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinRegex();

    public static bool IsValidPin(string? pin)
        => pin is not null && PinRegex().IsMatch(pin);

    public static string GeneratePin()
    {
        Span<byte> random = stackalloc byte[8];
        RandomNumberGenerator.Fill(random);
        ulong value = BitConverter.ToUInt64(random) % 1_000_000_000_000UL;
        return value.ToString("D12", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static LanRemotePinVerifier CreateVerifier(string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException("PIN must contain exactly 12 ASCII digits.", nameof(pin));

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSize);

        return new LanRemotePinVerifier
        {
            SaltBase64 = Convert.ToBase64String(salt),
            HashBase64 = Convert.ToBase64String(hash),
            Iterations = Iterations,
        };
    }

    public static bool Verify(string? pin, LanRemotePinVerifier? verifier)
    {
        if (!IsValidPin(pin) || verifier is null || verifier.Iterations <= 0)
            return false;

        try
        {
            byte[] salt = Convert.FromBase64String(verifier.SaltBase64);
            byte[] expected = Convert.FromBase64String(verifier.HashBase64);
            if (salt.Length != SaltSize || expected.Length != HashSize)
                return false;

            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                pin!, salt, verifier.Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record LanRemoteSession(
    string Id,
    string CsrfToken,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt);

internal sealed class LanRemoteSessionStore
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromHours(8);
    private readonly object _sync = new();
    private readonly Dictionary<string, LanRemoteSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public LanRemoteSessionStore(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public LanRemoteSession Create()
    {
        DateTimeOffset now = _clock();
        string id = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string csrf = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new LanRemoteSession(id, csrf, now, now);
        lock (_sync)
            _sessions[id] = session;
        return session;
    }

    public bool TryValidate(string? id, out LanRemoteSession session)
    {
        session = null!;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out LanRemoteSession? existing))
                return false;

            DateTimeOffset now = _clock();
            if (now - existing.LastSeenAt > IdleTimeout || now - existing.CreatedAt > AbsoluteTimeout)
            {
                _sessions.Remove(id);
                return false;
            }

            session = existing with { LastSeenAt = now };
            _sessions[id] = session;
            return true;
        }
    }

    public bool ValidateCsrf(string? id, string? token)
    {
        if (!TryValidate(id, out LanRemoteSession session) || string.IsNullOrEmpty(token))
            return false;

        byte[] expected = System.Text.Encoding.UTF8.GetBytes(session.CsrfToken);
        byte[] actual = System.Text.Encoding.UTF8.GetBytes(token);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public void Revoke(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        lock (_sync)
            _sessions.Remove(id);
    }

    public void Clear()
    {
        lock (_sync)
            _sessions.Clear();
    }
}

internal sealed class LanRemoteLoginRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _perIp = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _global = new();

    public bool TryAcquire(string ipAddress, DateTimeOffset now)
    {
        lock (_sync)
        {
            Trim(_global, now);
            if (!_perIp.TryGetValue(ipAddress, out Queue<DateTimeOffset>? perIp))
                _perIp[ipAddress] = perIp = new Queue<DateTimeOffset>();
            Trim(perIp, now);

            if (perIp.Count >= 5 || _global.Count >= 30)
                return false;

            perIp.Enqueue(now);
            _global.Enqueue(now);
            return true;
        }
    }

    private static void Trim(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.TryPeek(out DateTimeOffset timestamp) && now - timestamp >= Window)
            queue.Dequeue();
    }
}

internal static class LanRemoteNetwork
{
    public static bool IsPrivateLanAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return false;

        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 10) return true;
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;
        return bytes[0] == 192 && bytes[1] == 168;
    }

    public static bool IsEligible(IPAddress address, NetworkInterfaceType type, OperationalStatus status)
    {
        if (status != OperationalStatus.Up || !IsPrivateLanAddress(address))
            return false;

        return type is NetworkInterfaceType.Ethernet
            or NetworkInterfaceType.Wireless80211
            or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetFx
            or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.Ethernet3Megabit;
    }

    public static IReadOnlyList<IPAddress> GetEligibleAddresses()
    {
        var result = new HashSet<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel || nic.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (IsEligible(unicast.Address, nic.NetworkInterfaceType, nic.OperationalStatus))
                    result.Add(unicast.Address);
            }
        }
        return result.OrderBy(static a => a.ToString(), StringComparer.Ordinal).ToArray();
    }
}

internal static class LanRemotePortSelector
{
    public static int Select(int preferred, Func<int, bool> isAvailable)
    {
        int start = preferred is >= 1 and <= 65535 ? preferred : 51737;
        int end = start == 51737 ? 51747 : Math.Min(65535, start + 10);
        for (int port = start; port <= end; port++)
            if (isAvailable(port))
                return port;

        throw new InvalidOperationException("No available LAN remote-control port was found.");
    }
}
