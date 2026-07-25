using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VoltManager.Models;

namespace VoltManager.Services;

public class HardwareInfoService
{
    private SystemInfo? _cached;

    public SystemInfo GetSystemInfo()
    {
        if (_cached != null) return _cached;

        // Registry first (cheap); WMI only if the name key is missing.
        string cpu = ReadRegistryCpuName()
            ?? QueryFirst("Win32_Processor", "Name")
            ?? "CPU sconosciuta";
        string gpu = BestGpuName();
        double ramGb = ReadInstalledRamGb();

        bool hasBattery = false;
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent != 255;
            }
        }
        catch (Exception ex) { Logger.Warn("Battery presence query failed: " + ex.Message); }

        _cached = new SystemInfo
        {
            CpuName = cpu.Trim(),
            GpuName = gpu,
            RamTotalGb = ramGb,
            OsVersion = Environment.OSVersion.VersionString,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            HasBattery = hasBattery,
            LogicalCores = Environment.ProcessorCount,
        };
        return _cached;
    }

    /// <summary>
    /// OS-visible physical RAM via GlobalMemoryStatusEx, in whole GB.
    /// Avoids the cold WMI hit that Win32_ComputerSystem costs on the startup path.
    /// </summary>
    public static double ReadInstalledRamGb()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            return RoundInstalledRamGb(status.TotalPhysical);
        }
        catch (Exception ex) { Logger.Warn("Native RAM total query failed: " + ex.Message); }
        return 0;
    }

    /// <summary>
    /// Whole-GB rounding of OS-visible physical bytes (15.8 → 16, 15.4 → 16).
    /// Not installed capacity: a big iGPU carve-out still lands a 16 GB machine on 14.
    /// Ceiling, not Round — MonitorService derives RamUsedGb from the raw value, so rounding
    /// down would render "15,4 / 15 GB" whenever the fractional part exceeds .5.
    /// </summary>
    public static double RoundInstalledRamGb(ulong totalPhysicalBytes)
    {
        if (totalPhysicalBytes == 0) return 0;
        return Math.Ceiling(totalPhysicalBytes / (1024.0 * 1024 * 1024));
    }

    private static string BestGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController");
            string? best = null;
            int bestScore = int.MinValue;
            foreach (var mo in searcher.Get())
            {
                string? name = mo["Name"]?.ToString();
                string? pnp = mo["PNPDeviceID"]?.ToString();
                long ram = NormalizeAdapterRam(mo["AdapterRAM"]);
                int score = ScoreGpu(name, ram, pnp);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = name;
                }
            }
            if (best != null && bestScore > -500) return best.Trim();
            return best?.Trim() ?? "GPU sconosciuta";
        }
        catch (Exception ex)
        {
            Logger.Warn("GPU name query failed: " + ex.Message);
            return "GPU sconosciuta";
        }
    }

    /// <summary>
    /// AdapterRAM is often UINT32_MAX / -1 when the driver does not report VRAM (WDDM).
    /// Treat those as "unknown" so they do not outrank real adapters.
    /// </summary>
    public static long NormalizeAdapterRam(object? raw)
    {
        if (raw == null) return 0;
        try
        {
            long ram = Convert.ToInt64(raw);
            if (ram <= 0) return 0;
            // WMI AdapterRAM is UINT32; drivers often return 0xFFFFFFFF for "unknown".
            // Do not reject values > 4GB (other sources / corrected casts).
            if (ram == 0xFFFF_FFFFL) return 0;
            return ram;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Higher wins. Virtual/remote adapters score very low; PCI + known vendors + VRAM win.
    /// </summary>
    public static int ScoreGpu(string? name, long adapterRamBytes, string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(name)) return int.MinValue;
        string lower = name.Trim().ToLowerInvariant();

        // Display mirrors / remote / hypervisor adapters — never prefer these.
        if (IsVirtualOrSoftwareGpu(lower, pnpDeviceId)) return -1000;

        int score = 10;
        if (adapterRamBytes > 0)
            score += (int)Math.Min(adapterRamBytes / (1024 * 1024), 256_000); // MB, capped

        if (!string.IsNullOrEmpty(pnpDeviceId) &&
            pnpDeviceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            score += 100_000;

        if (ContainsAny(lower, "nvidia", "geforce", "quadro", "rtx ", "gtx ", "tesla", "titan"))
            score += 50_000;
        else if (ContainsAny(lower, "radeon", "amd ", "rx ", "firepro", "instinct"))
            score += 40_000;
        else if (ContainsAny(lower, "intel arc", "iris", "uhd graphics", "hd graphics", "intel(r) graphics"))
            score += 20_000;

        return score;
    }

    public static bool IsVirtualOrSoftwareGpu(string name, string? pnpDeviceId)
    {
        string nameLower = (name ?? "").Trim().ToLowerInvariant();
        if (nameLower.Length == 0) return true;
        if (ContainsAny(nameLower,
                "microsoft basic render",
                "microsoft remote display",
                "remote display adapter",
                "remote desktop",
                "indirect display",
                "idc mirror",
                "mirror driver",
                "virtual display",
                "virtualbox",
                "vmware",
                "hyper-v",
                "citrix",
                "parsec virtual",
                "teamviewer",
                "usb display",
                "spacedesk",
                "sunshine",
                "virtual gpu",
                "microsoft hyper-v video",
                "one display"))
            return true;

        if (!string.IsNullOrEmpty(pnpDeviceId))
        {
            string p = pnpDeviceId.ToUpperInvariant();
            if (p.StartsWith("SWD\\", StringComparison.Ordinal) ||
                p.StartsWith("ROOT\\", StringComparison.Ordinal) ||
                p.Contains("REMOTEDISPLAY") ||
                p.Contains("RDPIDD") ||
                p.Contains("MS_GDI") ||
                p.Contains("BASICDISPLAY") ||
                p.Contains("BASICRENDER"))
                return true;
        }
        return false;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string? ReadRegistryCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var value = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        catch (Exception ex) { Logger.Warn("CPU registry name failed: " + ex.Message); }
        return null;
    }

    private static string? QueryFirst(string cls, string prop)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var mo in searcher.Get())
                return mo[prop]?.ToString();
        }
        catch (Exception ex) { Logger.Warn($"WMI query {cls}.{prop} failed: " + ex.Message); }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
