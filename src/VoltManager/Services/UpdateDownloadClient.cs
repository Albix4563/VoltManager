using System.IO;
using System.Net.Http;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VoltManager.Services;

internal interface IUpdateFileSystem
{
    string UpdatesBasePath { get; }
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    void ApplyProtectedDirectoryAcl(string path);
    IEnumerable<string> EnumerateDirectories(string path);
    DateTime GetDirectoryCreationTimeUtc(string path);
    Stream CreateInstallerFile(string path);
    void DeleteDirectory(string path, bool recursive);

    // The loader opens the image without FILE_SHARE_WRITE: the handle held until launch
    // must be read-only, otherwise CreateProcess fails with a sharing violation.
    Stream ReopenInstallerForLaunch(string path, Stream written) => written;

    // Junctions/symlinks would redirect the ACL, the installer write or the recursive cleanup elsewhere.
    bool IsReparsePoint(string path) => false;
}

internal sealed class SystemUpdateFileSystem : IUpdateFileSystem
{
    public string UpdatesBasePath => VoltManagerArtifacts.UpdatesDirectory;

    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
    public DateTime GetDirectoryCreationTimeUtc(string path) => Directory.GetCreationTimeUtc(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public bool IsReparsePoint(string path)
        => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    public Stream CreateInstallerFile(string path)
        => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 81920, FileOptions.Asynchronous);

    public Stream ReopenInstallerForLaunch(string path, Stream written)
    {
        try { written.Flush(); }
        finally { written.Dispose(); }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
    }

    public void ApplyProtectedDirectoryAcl(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        // The owner keeps implicit WRITE_DAC: a directory owned by any other account (including the
        // interactive user, who could rewrite the DACL from a medium-integrity process) goes to Administrators.
        var directory = new DirectoryInfo(path);
        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (!IsTrustedOwner(owner))
            security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        directory.SetAccessControl(security);
    }

    internal static bool IsTrustedOwner(SecurityIdentifier? owner)
        => owner is not null &&
           (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            owner.IsWellKnown(WellKnownSidType.LocalSystemSid));
}

internal sealed class UpdateDownloadClient
{
    private const int MaxChecksumBytes = 64 * 1024;
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly TimeSpan _inactivityTimeout;
    private readonly Action<string> _errorLogger;
    private readonly Action<string> _warningLogger;

    internal UpdateDownloadClient(
        HttpClient http,
        IUpdateFileSystem fileSystem,
        TimeSpan inactivityTimeout,
        Action<string>? errorLogger = null,
        Action<string>? warningLogger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _inactivityTimeout = inactivityTimeout;
        _errorLogger = errorLogger ?? Logger.Error;
        _warningLogger = warningLogger ?? Logger.Warn;
    }

    internal async Task<VerifiedUpdateDownload> DownloadAsync(
        string url,
        string assetName,
        string? checksumUrl,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(checksumUrl))
            throw Reject($"Update rifiutato: asset SHA256SUMS mancante per {assetName}.");

        // The root is secured (and rejected if redirected) before the cleanup enumerates and deletes in it.
        string basePath = _fileSystem.UpdatesBasePath;
        EnsureSecureDirectory(basePath, protectOnlyWhenCreated: false);
        CleanupStaleDirectories();

        string runDirectory = Path.Combine(basePath, Guid.NewGuid().ToString("N"));
        EnsureSecureDirectory(runDirectory, protectOnlyWhenCreated: false);
        string randomSuffix = Guid.NewGuid().ToString("N")[..8];
        string destination = Path.Combine(runDirectory, $"VoltManagerSetup-{randomSuffix}.exe");

        Stream? target = null;
        try
        {
            target = _fileSystem.CreateInstallerFile(destination);
            await DownloadInstallerAsync(url, target, progress, cancellationToken).ConfigureAwait(false);

            string checksumDocument = await DownloadChecksumDocumentAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, string> checksums;
            try
            {
                checksums = Sha256SumsParser.Parse(checksumDocument);
            }
            catch (FormatException ex)
            {
                throw Reject("Update rifiutato: file SHA256SUMS non valido.", ex);
            }

            if (!checksums.TryGetValue(assetName, out string? expectedHash))
                throw Reject($"Update rifiutato: checksum SHA-256 mancante per {assetName}.");

            Stream written = target;
            target = null;
            target = _fileSystem.ReopenInstallerForLaunch(destination, written);
            string actualHash = ComputeSha256(target);
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw Reject($"Update rifiutato: checksum SHA-256 non corrispondente per {assetName}.");

            var verified = new VerifiedUpdateDownload(
                destination,
                runDirectory,
                assetName,
                expectedHash,
                target,
                _fileSystem,
                _warningLogger);
            target = null;
            return verified;
        }
        catch
        {
            target?.Dispose();
            TryDeleteRunDirectory(runDirectory);
            throw;
        }
    }

    private async Task DownloadInstallerAsync(
        string url,
        Stream target,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long received = 0;

        while (true)
        {
            int read = await ReadChunkAsync(source, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            if (total > 0)
                progress?.Invoke(Math.Min(99.9, Math.Round(received * 100.0 / total, 1)));
        }

        if (total > 0 && received != total)
            throw new EndOfStreamException($"Download incompleto: ricevuti {received} byte su {total}.");

        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Invoke(100);
    }

    private async Task<string> DownloadChecksumDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxChecksumBytes)
            throw Reject("Update rifiutato: file SHA256SUMS troppo grande.");

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var target = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int read = await ReadChunkAsync(source, buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (target.Length + read > MaxChecksumBytes)
                throw Reject("Update rifiutato: file SHA256SUMS troppo grande.");
            target.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(target.ToArray());
    }

    private void EnsureSecureDirectory(string path, bool protectOnlyWhenCreated)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent is not null && _fileSystem.DirectoryExists(parent))
            ThrowIfReparsePoint(parent);
        bool created = !_fileSystem.DirectoryExists(path);
        if (created)
            _fileSystem.CreateDirectory(path);
        if (!created && protectOnlyWhenCreated)
            return;

        try
        {
            ThrowIfReparsePoint(path);
            _fileSystem.ApplyProtectedDirectoryAcl(path);
        }
        catch (Exception ex)
        {
            if (created)
            {
                try { _fileSystem.DeleteDirectory(path, recursive: true); }
                catch (Exception cleanupEx) { _warningLogger($"Cleanup directory update fallita per {path}: {cleanupEx.Message}"); }
            }
            throw new InvalidOperationException($"Impossibile proteggere la directory aggiornamenti '{path}'.", ex);
        }
    }

    private void ThrowIfReparsePoint(string path)
    {
        if (_fileSystem.IsReparsePoint(path))
            throw new InvalidDataException($"La directory aggiornamenti '{path}' è un reparse point (junction/symlink).");
    }

    private void CleanupStaleDirectories()
    {
        string basePath = _fileSystem.UpdatesBasePath;
        if (!_fileSystem.DirectoryExists(basePath))
            return;

        try
        {
            DateTime cutoff = DateTime.UtcNow - StaleAge;
            foreach (string directory in _fileSystem.EnumerateDirectories(basePath))
            {
                try
                {
                    if (_fileSystem.IsReparsePoint(directory))
                    {
                        _warningLogger($"Cleanup aggiornamenti: reparse point ignorato {directory}");
                        continue;
                    }
                    if (_fileSystem.GetDirectoryCreationTimeUtc(directory) < cutoff)
                        _fileSystem.DeleteDirectory(directory, recursive: true);
                }
                catch (Exception ex)
                {
                    _warningLogger($"Cleanup aggiornamento obsoleto fallita per {directory}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _warningLogger("Cleanup directory aggiornamenti fallita: " + ex.Message);
        }
    }

    private void TryDeleteRunDirectory(string runDirectory)
    {
        try
        {
            if (_fileSystem.DirectoryExists(runDirectory))
                _fileSystem.DeleteDirectory(runDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _warningLogger($"Cleanup directory update fallita per {runDirectory}: {ex.Message}");
        }
    }

    private InvalidDataException Reject(string message, Exception? inner = null)
    {
        _errorLogger(message);
        return inner is null ? new InvalidDataException(message) : new InvalidDataException(message, inner);
    }

    internal static string ComputeSha256(Stream stream)
    {
        if (!stream.CanSeek)
            throw new InvalidOperationException("Lo stream dell'installer deve supportare Seek.");
        stream.Position = 0;
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        return hash;
    }

    private async Task<int> ReadChunkAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivity.CancelAfter(_inactivityTimeout);
        try
        {
            return await source.ReadAsync(buffer, inactivity.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && inactivity.IsCancellationRequested)
        {
            throw new TimeoutException("Download aggiornamento bloccato: nessun dato ricevuto entro il timeout.", ex);
        }
    }
}
