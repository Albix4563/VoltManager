using System.IO;

namespace VoltManager.Services;

public sealed class VerifiedUpdateDownload : IDisposable
{
    private readonly IUpdateFileSystem _fileSystem;
    private readonly Action<string> _warningLogger;
    private Stream? _stream;
    private bool _keepFiles;

    internal VerifiedUpdateDownload(
        string path,
        string runDirectory,
        string assetName,
        string expectedSha256,
        Stream stream,
        IUpdateFileSystem fileSystem,
        Action<string> warningLogger)
    {
        Path = path;
        RunDirectory = runDirectory;
        AssetName = assetName;
        ExpectedSha256 = expectedSha256;
        _stream = stream;
        _fileSystem = fileSystem;
        _warningLogger = warningLogger;
    }

    public string Path { get; }
    internal string RunDirectory { get; }
    internal string AssetName { get; }
    internal string ExpectedSha256 { get; }

    internal void VerifyOrThrow()
    {
        Stream stream = _stream ?? throw new ObjectDisposedException(nameof(VerifiedUpdateDownload));
        string actual = UpdateDownloadClient.ComputeSha256(stream);
        if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Update rifiutato: checksum SHA-256 non corrispondente per {AssetName}.");
    }

    internal void MarkLaunched()
    {
        _keepFiles = true;
        Dispose();
    }

    public void Dispose()
    {
        Stream? stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;

        stream.Dispose();
        if (_keepFiles)
            return;

        try
        {
            if (_fileSystem.DirectoryExists(RunDirectory))
                _fileSystem.DeleteDirectory(RunDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            _warningLogger($"Cleanup directory update fallita per {RunDirectory}: {ex.Message}");
        }
    }
}
