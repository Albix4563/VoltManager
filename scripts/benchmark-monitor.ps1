param([int]$Iterations = 200)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class NativeMemoryStatus
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX buffer);
}
'@

function Measure-Reader([scriptblock]$Reader) {
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    [GC]::Collect()
    $before = [GC]::GetTotalMemory($true)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $last = $null
    for ($i = 0; $i -lt $Iterations; $i++) { $last = & $Reader }
    $timer.Stop()
    [pscustomobject]@{
        elapsedMs = [math]::Round($timer.Elapsed.TotalMilliseconds, 2)
        retainedBytes = [math]::Max(0, [GC]::GetTotalMemory($true) - $before)
        percent = [math]::Round($last, 2)
    }
}

$wmi = Measure-Reader {
    $os = Get-CimInstance Win32_OperatingSystem -Property FreePhysicalMemory,TotalVisibleMemorySize
    100 * (1 - [double]$os.FreePhysicalMemory / [double]$os.TotalVisibleMemorySize)
}

$native = Measure-Reader {
    $status = [NativeMemoryStatus+MEMORYSTATUSEX]::new()
    if (-not [NativeMemoryStatus]::GlobalMemoryStatusEx($status)) {
        throw "GlobalMemoryStatusEx failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    }
    [double]$status.dwMemoryLoad
}

$result = [pscustomobject]@{
    iterations = $Iterations
    wmi = $wmi
    native = $native
    speedup = [math]::Round($wmi.elapsedMs / [math]::Max($native.elapsedMs, 0.01), 2)
}

if ($wmi.percent -lt 0 -or $wmi.percent -gt 100 -or $native.percent -lt 0 -or $native.percent -gt 100) {
    throw 'Percentuale RAM fuori intervallo.'
}

$result | ConvertTo-Json -Depth 3
