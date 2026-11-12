using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VoltManager.Services;

public sealed record StorageCleanupResult(bool Confirmed, int DeletedFiles, long FreedBytes, int SkippedCount, bool Partial);

internal static class StorageTempCleaner
{
    // ponytail: pulizia conservativa entro 20 s/250.000 elementi/128 livelli; usare Pulizia disco per interventi più estesi.
    internal static StorageCleanupResult Clean(IEnumerable<string> roots, CancellationToken cancellationToken,
        TimeSpan? maxDuration = null, int maxEntries = 250_000, DateTime? now = null)
    {
        var watch = Stopwatch.StartNew();
        var duration = maxDuration ?? TimeSpan.FromSeconds(20);
        var cutoff = (now ?? DateTime.UtcNow).AddDays(-7).ToFileTimeUtc();
        int visited = 0, deleted = 0, skipped = 0;
        long bytes = 0;
        bool partial = false;
        bool Stop() => cancellationToken.IsCancellationRequested || watch.Elapsed >= duration || visited >= maxEntries;
        void Skip() { skipped++; partial = true; }

        void Visit(string directory, int depth)
        {
            if (Stop()) { partial = true; return; }
            if (depth >= 128) { Skip(); return; }
            try
            {
                // Tutti gli antenati restano aperti senza condivisione scrittura/eliminazione:
                // nessun processo può sostituirli o trasformarli in junction durante la visita.
                using var pinned = Open(directory, 0x80, 1, 0x02200000);
                var info = Info(pinned);
                if ((info.Attributes & (0x400u | 0x10u)) != 0x10u) { Skip(); return; }
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (Stop()) { partial = true; break; }
                    visited++;
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { Skip(); continue; }
                        if ((attributes & FileAttributes.Directory) != 0) { Visit(path, depth + 1); continue; }
                        long length;
                        // OPEN_REPARSE_POINT impedisce di aprire il target se il file viene sostituito.
                        using (var file = Open(path, 0x10080, 0, 0x00200000))
                        {
                            var fileInfo = Info(file);
                            if ((fileInfo.Attributes & (0x400u | 0x10u)) != 0 || fileInfo.Links != 1 ||
                                FileTime(fileInfo.Created) >= cutoff || FileTime(fileInfo.Written) >= cutoff)
                            { skipped++; continue; }
                            if (cancellationToken.IsCancellationRequested || watch.Elapsed >= duration)
                            { partial = true; break; }
                            length = ((long)fileInfo.SizeHigh << 32) | fileInfo.SizeLow;
                            var disposition = new FileDisposition { Delete = 1 };
                            if (!SetFileInformationByHandle(file, 4, ref disposition, 1))
                                throw new Win32Exception(Marshal.GetLastWin32Error());
                        }
                        deleted++;
                        bytes += length;
                    }
                    catch (Exception ex) when (IsAccessError(ex)) { Skip(); }
                }
            }
            catch (Exception ex) when (IsAccessError(ex)) { Skip(); }
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Stop()) { partial = true; break; }
            var ancestors = new List<SafeFileHandle>();
            try
            {
                string full = Path.GetFullPath(root);
                string? volume = Path.GetPathRoot(full);
                if (volume == null || volume.Length != 3 || !char.IsAsciiLetter(volume[0]) ||
                    !Path.IsPathFullyQualified(root) || full.TrimEnd('\\').Length <= volume.Length)
                { Skip(); continue; }
                var chain = new Stack<string>();
                for (var parent = Directory.GetParent(full); parent != null; parent = parent.Parent)
                    chain.Push(parent.FullName);
                while (chain.TryPop(out var path))
                {
                    var handle = Open(path, 0x80, 1, 0x02200000);
                    ancestors.Add(handle);
                    if ((Info(handle).Attributes & (0x400u | 0x10u)) != 0x10u)
                        throw new IOException("Un antenato della cartella temporanea è un collegamento.");
                }
                Visit(full, 0);
            }
            catch (Exception ex) when (IsAccessError(ex) || ex is ArgumentException or NotSupportedException) { Skip(); }
            finally { foreach (var handle in ancestors.AsEnumerable().Reverse()) handle.Dispose(); }
        }
        return new(true, deleted, bytes, skipped, partial);
    }

    private static bool IsAccessError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or Win32Exception;

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static FileInformation Info(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var info)
        ? info : throw new Win32Exception(Marshal.GetLastWin32Error());

    private static long FileTime(System.Runtime.InteropServices.ComTypes.FILETIME time) =>
        ((long)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { public byte Delete; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref FileDisposition information, uint size);
}
