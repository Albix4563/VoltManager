using System.IO;
using System.Net.Http;

namespace VoltManager.Services;

internal interface IUpdateFileSystem
{
    string GetTempFilePath(string fileName);
    Stream CreateFile(string path);
    bool Exists(string path);
    void Delete(string path);
}

internal sealed class SystemUpdateFileSystem : IUpdateFileSystem
{
    public string GetTempFilePath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);
    public Stream CreateFile(string path) => File.Create(path);
    public bool Exists(string path) => File.Exists(path);
    public void Delete(string path) => File.Delete(path);
}

internal sealed class UpdateDownloadClient
{
    private readonly HttpClient _http;
    private readonly IUpdateFileSystem _fileSystem;
    private readonly TimeSpan _inactivityTimeout;

    internal UpdateDownloadClient(HttpClient http, IUpdateFileSystem fileSystem, TimeSpan inactivityTimeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _inactivityTimeout = inactivityTimeout;
    }

    internal async Task<string> DownloadAsync(
        string url,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        string destination = _fileSystem.GetTempFilePath("VoltManagerUpdate.exe");
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using Stream target = _fileSystem.CreateFile(destination);
            var buffer = new byte[81920];
            long received = 0;

            while (true)
            {
                int read = await ReadChunkAsync(source, buffer, cancellationToken);
                if (read == 0)
                    break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total > 0)
                    progress?.Invoke(Math.Min(99.9, Math.Round(received * 100.0 / total, 1)));
            }

            if (total > 0 && received != total)
                throw new EndOfStreamException($"Download incompleto: ricevuti {received} byte su {total}.");

            await target.FlushAsync(cancellationToken);
            progress?.Invoke(100);
            return destination;
        }
        catch
        {
            try
            {
                if (_fileSystem.Exists(destination))
                    _fileSystem.Delete(destination);
            }
            catch
            {
                // Cleanup must not replace the original failure.
            }
            throw;
        }
    }

    private async Task<int> ReadChunkAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        inactivity.CancelAfter(_inactivityTimeout);
        try
        {
            return await source.ReadAsync(buffer, inactivity.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && inactivity.IsCancellationRequested)
        {
            throw new TimeoutException("Download aggiornamento bloccato: nessun dato ricevuto entro il timeout.", ex);
        }
    }
}
