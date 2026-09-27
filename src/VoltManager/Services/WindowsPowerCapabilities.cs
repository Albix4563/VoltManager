using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace VoltManager.Services;

internal readonly record struct WindowsPowerCapabilityState(bool SleepAvailable, bool HibernateAvailable);

internal static class WindowsPowerCapabilities
{
    private const string PowerRegistryPath = @"SYSTEM\CurrentControlSet\Control\Power";
    private const string HiberFileTypeValueName = "HiberFileType";
    private const int HiberFileTypeReduced = 1;
    private const int HiberFileTypeFull = 2;

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(out SystemPowerCapabilities capabilities);

    public static WindowsPowerCapabilityState Query()
    {
        if (!OperatingSystem.IsWindows())
            return default;

        try
        {
            if (!GetPwrCapabilities(out SystemPowerCapabilities capabilities))
                return default;

            return Evaluate(
                capabilities.SystemS1 != 0,
                capabilities.SystemS2 != 0,
                capabilities.SystemS3 != 0,
                capabilities.SystemS4 != 0,
                capabilities.HiberFilePresent != 0,
                capabilities.AoAc != 0,
                ResolveHiberFileType(capabilities.HiberFileType));
        }
        catch
        {
            // Capability discovery is advisory. If Windows cannot be queried safely,
            // do not expose remote suspend commands.
            return default;
        }
    }

    internal static int NativeCapabilitiesSize => Marshal.SizeOf<SystemPowerCapabilities>();

    internal static WindowsPowerCapabilityState Evaluate(
        bool systemS1,
        bool systemS2,
        bool systemS3,
        bool systemS4,
        bool hiberFilePresent,
        bool aoAc,
        int? hiberFileType)
    {
        bool sleepAvailable = aoAc || systemS1 || systemS2 || systemS3;
        // A reduced hiberfile only backs fast startup; hibernate needs a confirmed full hiberfile.
        bool hibernateAvailable = systemS4 && hiberFilePresent && hiberFileType == HiberFileTypeFull;
        return new WindowsPowerCapabilityState(sleepAvailable, hibernateAvailable);
    }

    private static int? ResolveHiberFileType(byte nativeHiberFileType)
    {
        if (nativeHiberFileType is HiberFileTypeReduced or HiberFileTypeFull)
            return nativeHiberFileType;

        return ReadHiberFileType();
    }

    private static int? ReadHiberFileType()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PowerRegistryPath, writable: false);
        object? value = key?.GetValue(HiberFileTypeValueName);
        return value switch
        {
            int number => number,
            uint number when number <= int.MaxValue => (int)number,
            _ => null,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerCapabilities
    {
        public byte PowerButtonPresent;
        public byte SleepButtonPresent;
        public byte LidPresent;
        public byte SystemS1;
        public byte SystemS2;
        public byte SystemS3;
        public byte SystemS4;
        public byte SystemS5;
        public byte HiberFilePresent;
        public byte FullWake;
        public byte VideoDimPresent;
        public byte ApmPresent;
        public byte UpsPresent;
        public byte ThermalControl;
        public byte ProcessorThrottle;
        public byte ProcessorMinThrottle;
        public byte ProcessorMaxThrottle;
        public byte FastSystemS4;
        public byte Hiberboot;
        public byte WakeAlarmPresent;
        public byte AoAc;
        public byte DiskSpinDown;
        public byte HiberFileType;
        public byte AoAcConnectivitySupported;
        public byte Spare3_0;
        public byte Spare3_1;
        public byte Spare3_2;
        public byte Spare3_3;
        public byte Spare3_4;
        public byte Spare3_5;
        public byte SystemBatteriesPresent;
        public byte BatteriesAreShortTerm;
        public BatteryReportingScale BatteryScale0;
        public BatteryReportingScale BatteryScale1;
        public BatteryReportingScale BatteryScale2;
        public int AcOnLineWake;
        public int SoftLidWake;
        public int RtcWake;
        public int MinDeviceWakeState;
        public int DefaultLowLatencyWake;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryReportingScale
    {
        public uint Granularity;
        public uint Capacity;
    }
}
