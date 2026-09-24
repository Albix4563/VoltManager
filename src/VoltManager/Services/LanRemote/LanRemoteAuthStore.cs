using System.IO;
using System.Text.Json;
using VoltManager.Models;

namespace VoltManager.Services.LanRemote;

internal sealed class LanRemoteAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private LanRemotePinVerifier? _verifier;

    public LanRemoteAuthStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            ValidationEnvironment.ApplicationDataRoot,
            "VoltManager",
            "remote-control-auth.json");
        _verifier = Load();
    }

    public bool HasPin
    {
        get { lock (_sync) return _verifier is not null; }
    }

    public void SetPin(string pin)
    {
        LanRemotePinVerifier verifier = LanRemotePinAuth.CreateVerifier(pin);
        lock (_sync)
        {
            Persist(verifier);
            _verifier = verifier;
        }
    }

    public bool Verify(string? pin)
    {
        lock (_sync)
            return LanRemotePinAuth.Verify(pin, _verifier);
    }

    private LanRemotePinVerifier? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;
            string json = File.ReadAllText(_path);
            LanRemotePinVerifier? verifier = JsonSerializer.Deserialize<LanRemotePinVerifier>(json, JsonOptions);
            if (verifier is null)
                return null;
            if (Convert.FromBase64String(verifier.SaltBase64).Length != 16
                || Convert.FromBase64String(verifier.HashBase64).Length != 32
                || verifier.Iterations != LanRemotePinAuth.Iterations
                || verifier.Digits != LanRemotePinAuth.PinLength)
            {
                return null;
            }
            return verifier;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            Logger.Warn("Could not load LAN remote-control authentication verifier: " + ex.Message);
            return null;
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
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}
