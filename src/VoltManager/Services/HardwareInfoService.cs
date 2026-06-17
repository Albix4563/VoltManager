using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public class HardwareInfoService
{
    private SystemInfo? _cached;

    public SystemInfo GetSystemInfo()
    {
        if (_cached != null) return _cached;

        string cpu = QueryFirst("Win32_Processor", "Name") ?? "CPU sconosciuta";
        string gpu = BestGpuName();
        double ramGb = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var mo in searcher.Get())
            {
                ramGb = Math.Round(Convert.ToDouble(mo["TotalPhysicalMemory"]) / (1024.0 * 1024 * 1024), 1);
                break;
            }
        }
        catch { }

        bool hasBattery = false;
        try
        {
            if (GetSystemPowerStatus(out var status))
            {
                hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryLifePercent != 255;
            }
        }
        catch { }

        _cached = new SystemInfo
        {
            CpuName = cpu.Trim(),
            GpuName = gpu,
            RamTotalGb = ramGb,
            OsVersion = Environment.OSVersion.VersionString,
            AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            HasBattery = hasBattery,
        };
        return _cached;
    }

    private static string BestGpuName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            string? best = null;
            long bestRam = -1;
            foreach (var mo in searcher.Get())
            {
                string? name = mo["Name"]?.ToString();
                long ram = 0;
                try { ram = Convert.ToInt64(mo["AdapterRAM"] ?? 0); } catch { }
                if (name != null && ram >= bestRam)
                {
                    best = name;
                    bestRam = ram;
                }
            }
            return best ?? "GPU sconosciuta";
        }
        catch
        {
            return "GPU sconosciuta";
        }
    }

    private static string? QueryFirst(string cls, string prop)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}");
            foreach (var mo in searcher.Get())
                return mo[prop]?.ToString();
        }
        catch { }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

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
}
