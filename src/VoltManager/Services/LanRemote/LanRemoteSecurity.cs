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
    public const int MinSecretLength = 8;
    public const int MaxSecretLength = 64;
    public const int GeneratedSecretLength = 10;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string GeneratedSecretAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    [GeneratedRegex("^[A-Za-z0-9]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinRegex();

    public static bool IsValidPin(string? pin)
        => pin is not null && PinRegex().IsMatch(pin);

    public static string GeneratePin()
    {
        Span<char> secret = stackalloc char[GeneratedSecretLength];
        for (int i = 0; i < secret.Length; i++)
            secret[i] = GeneratedSecretAlphabet[RandomNumberGenerator.GetInt32(GeneratedSecretAlphabet.Length)];
        return new string(secret);
    }

    public static LanRemotePinVerifier CreateVerifier(string pin)
    {
        if (!IsValidPin(pin))
            throw new ArgumentException(
                $"Access secret must contain {MinSecretLength}-{MaxSecretLength} ASCII letters or digits.",
                nameof(pin));

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
            MinLength = MinSecretLength,
            MaxLength = MaxSecretLength,
        };
    }

    public static bool Verify(string? pin, LanRemotePinVerifier? verifier)
    {
        if (!IsValidPin(pin)
            || verifier is null
            || string.IsNullOrEmpty(verifier.SaltBase64)
            || string.IsNullOrEmpty(verifier.HashBase64)
            || !IsCurrentVerifier(verifier))
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

    public static bool IsCurrentVerifier(LanRemotePinVerifier verifier)
        => verifier.Iterations == Iterations
           && verifier.MinLength == MinSecretLength
           && verifier.MaxLength == MaxSecretLength
           && verifier.Digits == 0;

    public static bool IsLegacyVerifier(LanRemotePinVerifier verifier)
        => verifier.Iterations == Iterations
           && verifier.MinLength == 0
           && verifier.MaxLength == 0
           && verifier.Digits == 4;
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

internal readonly record struct LanRemoteRateLimitResult(bool Allowed, TimeSpan RetryAfter)
{
    public static LanRemoteRateLimitResult Permit => new(true, TimeSpan.Zero);
}

internal sealed class LanRemoteLoginRateLimiter
{
    internal const int FailureThreshold = 5;
    internal const int GlobalAttemptLimit = 30;
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BaseLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan EntryRetention = TimeSpan.FromMinutes(15);

    private sealed class IpState
    {
        public int ConsecutiveFailures;
        public int LockoutLevel;
        public DateTimeOffset? LockedUntil;
        public DateTimeOffset LastActivity;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, IpState> _perIp = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _global = new();
    private readonly Func<DateTimeOffset> _clock;

    public LanRemoteLoginRateLimiter(Func<DateTimeOffset>? clock = null)
        => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    internal int TrackedIpCount
    {
        get
        {
            lock (_sync)
            {
                Prune(_clock());
                return _perIp.Count;
            }
        }
    }

    public LanRemoteRateLimitResult TryAcquire(string ipAddress, bool hasValidSession = false)
    {
        DateTimeOffset now = _clock();
        lock (_sync)
        {
            Prune(now);
            // A valid session only skips the per-IP lockout (a legitimate user behind a
            // locked address can still sign in); the global cap always applies so a session
            // holder cannot drive unlimited PBKDF2 verifications.
            if (!hasValidSession
                && _perIp.TryGetValue(ipAddress, out IpState? state)
                && state.LockedUntil is DateTimeOffset lockedUntil
                && lockedUntil > now)
            {
                return new LanRemoteRateLimitResult(false, lockedUntil - now);
            }

            if (_global.Count >= GlobalAttemptLimit)
            {
                DateTimeOffset availableAt = _global.Peek() + GlobalWindow;
                return new LanRemoteRateLimitResult(false, availableAt > now ? availableAt - now : TimeSpan.FromSeconds(1));
            }

            _global.Enqueue(now);
            return LanRemoteRateLimitResult.Permit;
        }
    }

    public void RecordSuccess(string ipAddress)
    {
        lock (_sync)
        {
            Prune(_clock());
            _perIp.Remove(ipAddress);
        }
    }

    public void RecordFailure(string ipAddress)
    {
        DateTimeOffset now = _clock();
        lock (_sync)
        {
            Prune(now);
            if (!_perIp.TryGetValue(ipAddress, out IpState? state))
            {
                state = new IpState();
                _perIp[ipAddress] = state;
            }

            if (state.LockedUntil is DateTimeOffset lockedUntil && lockedUntil > now)
            {
                state.LastActivity = now;
                return;
            }

            state.LockedUntil = null;
            state.LastActivity = now;
            state.ConsecutiveFailures++;
            if (state.ConsecutiveFailures < FailureThreshold)
                return;

            state.ConsecutiveFailures = 0;
            double seconds = BaseLockout.TotalSeconds * Math.Pow(2, state.LockoutLevel);
            TimeSpan duration = TimeSpan.FromSeconds(Math.Min(seconds, MaxLockout.TotalSeconds));
            state.LockoutLevel++;
            state.LockedUntil = now + duration;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_global.TryPeek(out DateTimeOffset timestamp) && now - timestamp >= GlobalWindow)
            _global.Dequeue();

        foreach ((string ip, IpState state) in _perIp.ToArray())
        {
            if (state.LockedUntil is DateTimeOffset lockedUntil && lockedUntil <= now)
            {
                if (lockedUntil > state.LastActivity)
                    state.LastActivity = lockedUntil;
                state.LockedUntil = null;
            }

            if (state.LockedUntil is null && now - state.LastActivity >= EntryRetention)
                _perIp.Remove(ip);
        }
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
