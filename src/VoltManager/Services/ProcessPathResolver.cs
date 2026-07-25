using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoltManager.Services;

/// <summary>
/// Resolves a process image path. MainModule fails for WOW64, protected, or
/// cross-session processes; QueryFullProcessImageName covers most of those.
/// </summary>
public static class ProcessPathResolver
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, char[] lpExeName, ref int lpdwSize);

    public static string TryGetPath(Process process)
    {
        // Native query first: MainModule enumerates *every* loaded module of the target
        // process and throws on protected ones, which costs orders of magnitude more.
        string native = TryQueryFullProcessImageName(process.Id);
        if (!string.IsNullOrWhiteSpace(native))
            return native;

        try
        {
            string? main = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(main))
                return main;
        }
        catch
        {
            // MainModule denied too — caller treats an empty path as "unknown".
        }

        return "";
    }

    public static string TryQueryFullProcessImageName(int processId)
    {
        IntPtr handle = IntPtr.Zero;
        char[]? rented = null;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return "";

            rented = ArrayPool<char>.Shared.Rent(1024);
            int size = rented.Length;
            if (!QueryFullProcessImageName(handle, 0, rented, ref size) || size <= 0)
                return "";

            return new string(rented, 0, Math.Min(size, rented.Length));
        }
        catch
        {
            return "";
        }
        finally
        {
            if (rented != null)
                ArrayPool<char>.Shared.Return(rented);
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
