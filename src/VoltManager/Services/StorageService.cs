using System.Diagnostics;
using System.IO;
using System.Security;

namespace VoltManager.Services;

public sealed record StorageDriveInfo(string Root, string Label, string Format, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

public sealed record StorageEntryInfo(string Name, bool IsDirectory)
{
    public long Bytes { get; internal set; }
}

public sealed record StorageAnalysisResult(string Root, IReadOnlyList<StorageEntryInfo> Entries,
    long ScannedBytes, int SkippedCount, bool Partial);

public sealed class StorageService
{
    private readonly Func<DriveInfo[]> _getDrives;
    private int _scanning;

    public StorageService() : this(DriveInfo.GetDrives) { }
    internal StorageService(Func<DriveInfo[]> getDrives) => _getDrives = getDrives;

    public IReadOnlyList<StorageDriveInfo> GetStorageDrives()
    {
        var result = new List<StorageDriveInfo>();
        foreach (var drive in _getDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                long total = drive.TotalSize;
                long free = drive.TotalFreeSpace;
                result.Add(new(drive.RootDirectory.FullName, drive.VolumeLabel, drive.DriveFormat, total, free));
            }
            catch (Exception ex) when (IsAccessError(ex)) { /* Unità rimossa o non accessibile. */ }
        }
        return result;
    }

    public Task<StorageAnalysisResult> GetStorageAnalysisAsync(string root, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(ValidateRoot(root), cancellationToken), cancellationToken);

    internal string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length != 3 || !char.IsAsciiLetter(root[0]) ||
            root[1] != ':' || root[2] is not ('\\' or '/'))
            throw new ArgumentException("Specificare la radice di un’unità locale, per esempio C:\\.", nameof(root));
        string normalized = char.ToUpperInvariant(root[0]) + ":\\";
        var drive = GetStorageDrives().FirstOrDefault(item =>
            string.Equals(item.Root, normalized, StringComparison.OrdinalIgnoreCase));
        if (drive == null || (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Unità locale non disponibile o collegamento non supportato.", nameof(root));
        return normalized;
    }

    private StorageAnalysisResult Scan(string root, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
            throw new InvalidOperationException("Un’analisi è già in corso.");
        try { return ScanDirectory(root, cancellationToken); }
        finally { Volatile.Write(ref _scanning, 0); }
    }

    // ponytail: analisi limitata a 20 s, 250.000 elementi e 1.000 voci radice; aggiungere analisi incrementale solo se richiesta.
    internal static StorageAnalysisResult ScanDirectory(string root, CancellationToken cancellationToken,
        TimeSpan? maxDuration = null, int maxEntries = 250_000, int maxRootEntries = 1_000)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        var duration = maxDuration ?? TimeSpan.FromSeconds(20);
        var entries = new List<StorageEntryInfo>();
        var pending = new Stack<(DirectoryInfo Directory, StorageEntryInfo? Owner)>();
        pending.Push((new DirectoryInfo(root), null));
        int visited = 0;
        int skipped = 0;
        bool partial = false;
        bool limitReached = false;

        void Skip() { skipped++; partial = true; }
        bool Stop()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed < duration && visited < maxEntries) return false;
            partial = limitReached = true;
            return true;
        }

        IEnumerable<FileSystemInfo> Enumerate(DirectoryInfo directory)
        {
            IEnumerator<FileSystemInfo> iterator;
            try
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { Skip(); yield break; }
                iterator = directory.EnumerateFileSystemInfos().GetEnumerator();
            }
            catch (Exception ex) when (IsAccessError(ex)) { Skip(); yield break; }
            using (iterator)
            {
                while (!Stop())
                {
                    FileSystemInfo item;
                    try
                    {
                        if (!iterator.MoveNext()) yield break;
                        item = iterator.Current;
                    }
                    catch (Exception ex) when (IsAccessError(ex)) { Skip(); yield break; }
                    yield return item;
                }
            }
        }

        while (pending.Count > 0 && !Stop())
        {
            var (directory, owner) = pending.Pop();
            foreach (var item in Enumerate(directory))
            {
                visited++;
                try
                {
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) { Skip(); continue; }
                    bool isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                    var entry = owner;
                    if (entry == null)
                    {
                        if (entries.Count >= maxRootEntries) { Skip(); limitReached = true; break; }
                        entry = new StorageEntryInfo(item.Name, isDirectory);
                        entries.Add(entry);
                    }
                    if (isDirectory) pending.Push((new DirectoryInfo(item.FullName), entry));
                    else entry.Bytes = checked(entry.Bytes + ((FileInfo)item).Length);
                }
                catch (Exception ex) when (IsAccessError(ex)) { Skip(); }
            }
            if (limitReached) break;
        }
        var sorted = entries.OrderByDescending(entry => entry.Bytes)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new(root, sorted, sorted.Sum(entry => entry.Bytes), skipped, partial);
    }

    private static bool IsAccessError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;
}
