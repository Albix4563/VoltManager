using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

/// <summary>
/// Reads detailed memory metrics (in-use, standby, free) and can purge the
/// Windows standby list — the same technique used by Sysinternals RAMMap.
/// Requires administrator rights (the app already runs elevated) and
/// SeProfileSingleProcessPrivilege (present in the default admin token).
/// </summary>
public class MemoryOptimizerService
{
    // ── P/Invoke declarations ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;        // % in use
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ── Performance counter for standby (cached) pages ────────────────────────
    // "Memory\Standby Cache Normal Priority Bytes" + "Reserve Bytes" + "Core Bytes"
    // Summing all three gives the full standby list visible in Task Manager.
    private static readonly PerformanceCounter? StandbyCore =
        TryCounter("Memory", "Standby Cache Core Bytes");
    private static readonly PerformanceCounter? StandbyNormal =
        TryCounter("Memory", "Standby Cache Normal Priority Bytes");
    private static readonly PerformanceCounter? StandbyReserve =
        TryCounter("Memory", "Standby Cache Reserve Bytes");

    private static PerformanceCounter? TryCounter(string cat, string name)
    {
        try { return new PerformanceCounter(cat, name, true); }
        catch { return null; }
    }

    // ── NtSetSystemInformation for purge ──────────────────────────────────────
    // infoClass 80 = SystemMemoryListInformation, command value 4 = MemoryPurgeStandbyList
    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    private const int SystemMemoryListInformation = 80;
    private const int MemoryPurgeStandbyList      = 4;

    // ── Privilege adjustment ──────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr hProcess, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength,
        IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY             = 0x0008;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a snapshot of the current memory layout (total, in-use, standby, free).
    /// </summary>
    public MemoryStatus GetMemoryStatus()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        double total   = ms.ullTotalPhys / 1_073_741_824.0;
        double avail   = ms.ullAvailPhys / 1_073_741_824.0;

        // Standby = sum of all standby-list performance counters
        double standbyBytes = 0;
        try
        {
            standbyBytes += StandbyCore?.NextValue()   ?? 0;
            standbyBytes += StandbyNormal?.NextValue() ?? 0;
            standbyBytes += StandbyReserve?.NextValue() ?? 0;
        }
        catch { /* counters may throw on some editions */ }
        double standby = standbyBytes / 1_073_741_824.0;
        standby = Math.Min(standby, avail); // sanity-cap to available physical

        double free   = Math.Max(0, avail - standby);
        double inUse  = Math.Max(0, total - avail);

        return new MemoryStatus
        {
            TotalGb   = Math.Round(total,   2),
            InUseGb   = Math.Round(inUse,   2),
            StandbyGb = Math.Round(standby, 2),
            FreeGb    = Math.Round(free,    2),
            InUsePct  = total > 0 ? Math.Round(inUse  / total * 100, 1) : 0,
            StandbyPct = total > 0 ? Math.Round(standby / total * 100, 1) : 0,
        };
    }

    /// <summary>
    /// Purges the Windows standby (cached) memory list.
    /// Requires SeProfileSingleProcessPrivilege — already present in the admin token.
    /// Returns true if the NT call succeeded.
    /// </summary>
    public bool PurgeStandbyList()
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");

        int command = MemoryPurgeStandbyList;
        int status  = NtSetSystemInformation(
            SystemMemoryListInformation,
            ref command,
            sizeof(int));

        // STATUS_SUCCESS = 0
        return status == 0;
    }

    // ── Privilege helper ─────────────────────────────────────────────────────

    private static void EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                              TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY,
                              out IntPtr token))
            return;

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                return;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED,
                },
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }
}
