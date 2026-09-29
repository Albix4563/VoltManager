using System.IO;
using System.Text.Json;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

internal enum LanRemoteAuthStoreStatus
{
    Missing,
    Valid,
    Legacy,
    Corrupt,
}

internal sealed class LanRemoteAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private LanRemotePinVerifier? _verifier;
    private LanRemoteAuthStoreStatus _status;

    public LanRemoteAuthStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            ValidationEnvironment.ApplicationDataRoot,
            "VoltManager",
            "remote-control-auth.json");
        (_verifier, _status) = Load();
    }

    public bool HasPin
    {
        get { lock (_sync) return _verifier is not null; }
    }

    public bool PinNeedsRegeneration
    {
        get { lock (_sync) return _status == LanRemoteAuthStoreStatus.Legacy; }
    }

    public bool IsCorrupt
    {
        get { lock (_sync) return _status == LanRemoteAuthStoreStatus.Corrupt; }
    }

    internal LanRemoteAuthStoreStatus Status
    {
        get { lock (_sync) return _status; }
    }

    public void SetPin(string pin)
    {
        LanRemotePinVerifier verifier = LanRemotePinAuth.CreateVerifier(pin);
        lock (_sync)
        {
            Persist(verifier);
            _verifier = verifier;
            _status = LanRemoteAuthStoreStatus.Valid;
        }
    }

    public bool Verify(string? pin)
    {
        lock (_sync)
            return _status == LanRemoteAuthStoreStatus.Valid && LanRemotePinAuth.Verify(pin, _verifier);
    }

    private (LanRemotePinVerifier? Verifier, LanRemoteAuthStoreStatus Status) Load()
    {
        try
        {
            if (!File.Exists(_path))
                return (null, LanRemoteAuthStoreStatus.Missing);
            string json = File.ReadAllText(_path);
            LanRemotePinVerifier? verifier = JsonSerializer.Deserialize<LanRemotePinVerifier>(json, JsonOptions);
            if (verifier is null)
            {
                Logger.Warn("LAN remote-control authentication verifier is corrupt.");
                return (null, LanRemoteAuthStoreStatus.Corrupt);
            }
            if (string.IsNullOrEmpty(verifier.SaltBase64)
                || string.IsNullOrEmpty(verifier.HashBase64)
                || Convert.FromBase64String(verifier.SaltBase64).Length != 16
                || Convert.FromBase64String(verifier.HashBase64).Length != 32
                || verifier.Iterations != LanRemotePinAuth.Iterations)
            {
                Logger.Warn("LAN remote-control authentication verifier is corrupt.");
                return (null, LanRemoteAuthStoreStatus.Corrupt);
            }

            if (LanRemotePinAuth.IsCurrentVerifier(verifier))
                return (verifier, LanRemoteAuthStoreStatus.Valid);
            if (LanRemotePinAuth.IsLegacyVerifier(verifier))
                return (verifier, LanRemoteAuthStoreStatus.Legacy);

            Logger.Warn("LAN remote-control authentication verifier uses unsupported parameters.");
            return (null, LanRemoteAuthStoreStatus.Corrupt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Logger.Warn("Could not load LAN remote-control authentication verifier: " + ex.Message);
            return (null, LanRemoteAuthStoreStatus.Corrupt);
        }
    }

    private void Persist(LanRemotePinVerifier verifier)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(verifier, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { /* best-effort: a stale temporary verifier file is harmless. */ }
        }
    }
}
