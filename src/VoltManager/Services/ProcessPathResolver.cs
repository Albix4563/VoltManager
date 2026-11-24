using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace VoltManager.Services;

/// <summary>
/// Resolves a process image path. MainModule fails for WOW64, protected, or
/// cross-session processes; QueryFullProcessImageName covers most of those.
/// </summary>
public static partial class ProcessPathResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(
        uint processAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        [Out] ushort[] lpExeName,
        ref int lpdwSize);

    public static string TryQueryFullProcessImageName(int processId)
    {
        IntPtr handle = IntPtr.Zero;
        ushort[]? rented = null;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return "";

            rented = ArrayPool<ushort>.Shared.Rent(1024);
            int size = rented.Length;
            if (!QueryFullProcessImageName(handle, 0, rented, ref size) || size <= 0)
                return "";

            return new string(MemoryMarshal.Cast<ushort, char>(rented.AsSpan(0, Math.Min(size, rented.Length))));
        }
        catch
        {
            return "";
        }
        finally
        {
            if (rented != null)
                ArrayPool<ushort>.Shared.Return(rented);
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    // The scanners normalize the same few hundred image paths every few seconds and
    // each call allocates three intermediate strings. Memoize: the result for a given
    // input never changes. Bounded so a pathological run cannot grow it without limit.
    private const int NormalizeCacheLimit = 1024;
    private static readonly ConcurrentDictionary<string, string> NormalizeCache = new(StringComparer.Ordinal);

    public static string Normalize(string path)
    {
        string key = path ?? "";
        if (NormalizeCache.TryGetValue(key, out var cached)) return cached;

        string normalized = NormalizeCore(key);
        if (NormalizeCache.Count >= NormalizeCacheLimit) NormalizeCache.Clear();
        NormalizeCache[key] = normalized;
        return normalized;
    }

    private static string NormalizeCore(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path))
                .Trim()
                .Trim('"')
                .ToLowerInvariant();
        }
        catch
        {
            return path.Trim().Trim('"').ToLowerInvariant();
        }
    }
}
