using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VoltManager.Services;

public sealed record DisplayInfo(
    string Id,
    int Number,
    string Name,
    PixelRect WorkArea,
    double DpiScaleX,
    double DpiScaleY,
    bool IsPrimary);

public sealed record DisplaySnapshot(IReadOnlyList<DisplayInfo> Displays, bool IsReliable)
{
    public DisplayInfo Primary =>
        Displays.FirstOrDefault(d => d.IsPrimary)
        ?? Displays.FirstOrDefault()
        ?? new DisplayInfo("primary-fallback", 1, @"\\.\DISPLAY1",
            new PixelRect(0, 0, 1920, 1080), 1, 1, true);
}

internal sealed class DisplayService : IDisposable
{
    private readonly DispatcherTimer _changeTimer;
    private readonly EventHandler _displaySettingsHandler;
    private bool _disposed;

    public event Action<DisplaySnapshot>? DisplaysChanged;

    public DisplayService()
    {
        _changeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _changeTimer.Tick += (_, _) =>
        {
            _changeTimer.Stop();
            try { DisplaysChanged?.Invoke(GetSnapshot()); }
            catch (Exception ex) { Logger.Warn("Display change notification failed: " + ex.Message); }
        };

        _displaySettingsHandler = OnDisplaySettingsChanged;
        SystemEvents.DisplaySettingsChanged += _displaySettingsHandler;
    }

    public DisplaySnapshot GetSnapshot()
    {
        try
        {
            var displays = EnumerateDisplays()
                .OrderBy(d => d.Number)
                .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (displays.Length > 0)
                return new DisplaySnapshot(displays, true);
        }
        catch (Exception ex)
        {
            Logger.Warn("Display enumeration failed: " + ex.Message);
        }

        return SyntheticPrimary();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _changeTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= _displaySettingsHandler;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            _changeTimer.Stop();
            _changeTimer.Start();
        });
    }

    private static DisplaySnapshot SyntheticPrimary()
    {
        var work = SystemParameters.WorkArea;
        // SystemParameters.WorkArea is DIP; treat as pixels at scale 1 for fallback.
        return new DisplaySnapshot(
        [
            new DisplayInfo(
                "primary-fallback",
                1,
                @"\\.\DISPLAY1",
                new PixelRect(work.Left, work.Top, work.Width, work.Height),
                1,
                1,
                true),
        ],
        false);
    }

    private static List<DisplayInfo> EnumerateDisplays()
    {
        var monitors = new List<NativeMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(hMonitor, ref info)) return true;

                uint dpiX = 96, dpiY = 96;
                try { GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY); }
                catch { /* fallback 96 */ }

                string device = info.szDevice?.TrimEnd('\0') ?? "";
                int number = ParseDisplayNumber(device);
                monitors.Add(new NativeMonitor(
                    hMonitor,
                    device,
                    number,
                    new PixelRect(
                        info.rcWork.Left,
                        info.rcWork.Top,
                        info.rcWork.Right - info.rcWork.Left,
                        info.rcWork.Bottom - info.rcWork.Top),
                    dpiX / 96.0,
                    dpiY / 96.0,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
                return true;
            },
            IntPtr.Zero);

        var nameMap = QueryDisplayNames();

        // Assign stable numbers for devices without \\.\DISPLAYn suffix.
        int next = 1;
        var used = new HashSet<int>(monitors.Where(m => m.Number > 0).Select(m => m.Number));
        foreach (var m in monitors.OrderBy(m => m.Device, StringComparer.OrdinalIgnoreCase))
        {
            if (m.Number > 0) continue;
            while (used.Contains(next)) next++;
            m.Number = next;
            used.Add(next);
            next++;
        }

        var result = new List<DisplayInfo>(monitors.Count);
        foreach (var m in monitors)
        {
            string id = m.Device;
            string name = m.Device;
            if (nameMap.TryGetValue(m.Device, out var mapped))
            {
                if (!string.IsNullOrWhiteSpace(mapped.Id)) id = mapped.Id;
                if (!string.IsNullOrWhiteSpace(mapped.Name)) name = mapped.Name;
            }

            if (string.IsNullOrWhiteSpace(id)) id = m.Device;
            if (string.IsNullOrWhiteSpace(name)) name = m.Device;

            result.Add(new DisplayInfo(
                id,
                m.Number,
                name,
                m.WorkArea,
                m.DpiScaleX <= 0 ? 1 : m.DpiScaleX,
                m.DpiScaleY <= 0 ? 1 : m.DpiScaleY,
                m.IsPrimary));
        }

        return result;
    }

    private static int ParseDisplayNumber(string device)
    {
        // \\.\DISPLAY1 → 1
        if (string.IsNullOrEmpty(device)) return 0;
        int idx = device.LastIndexOf("DISPLAY", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        string tail = device[(idx + "DISPLAY".Length)..];
        return int.TryParse(tail, out int n) && n > 0 ? n : 0;
    }

    private static Dictionary<string, (string Id, string Name)> QueryDisplayNames()
    {
        var map = new Dictionary<string, (string Id, string Name)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            int pathCount = 0, modeCount = 0;
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount);
            if (err != 0 || pathCount <= 0) return map;

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (err != 0) return map;

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];

                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                sourceName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                sourceName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                sourceName.header.adapterId = path.sourceInfo.adapterId;
                sourceName.header.id = path.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref sourceName) != 0) continue;

                string gdi = sourceName.viewGdiDeviceName?.TrimEnd('\0') ?? "";
                if (string.IsNullOrEmpty(gdi)) continue;

                var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                targetName.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                targetName.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                targetName.header.adapterId = path.targetInfo.adapterId;
                targetName.header.id = path.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref targetName) != 0) continue;

                string friendly = targetName.monitorFriendlyDeviceName?.TrimEnd('\0') ?? "";
                string devicePath = targetName.monitorDevicePath?.TrimEnd('\0') ?? "";
                map[gdi] = (
                    string.IsNullOrWhiteSpace(devicePath) ? gdi : devicePath,
                    string.IsNullOrWhiteSpace(friendly) ? gdi : friendly);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("QueryDisplayConfig failed: " + ex.Message);
        }

        return map;
    }

    private sealed class NativeMonitor
    {
        public IntPtr Handle;
        public string Device;
        public int Number;
        public PixelRect WorkArea;
        public double DpiScaleX;
        public double DpiScaleY;
        public bool IsPrimary;

        public NativeMonitor(IntPtr handle, string device, int number, PixelRect workArea,
            double dpiScaleX, double dpiScaleY, bool isPrimary)
        {
            Handle = handle;
            Device = device;
            Number = number;
            WorkArea = workArea;
            DpiScaleX = dpiScaleX;
            DpiScaleY = dpiScaleY;
            IsPrimary = isPrimary;
        }
    }

    private const int MONITORINFOF_PRIMARY = 1;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint QDC_ONLY_ACTIVE_PATHS = 2;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
    private const int DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int numPathArrayElements, out int numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref int numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref int numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public bool targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        public uint infoType;
        public uint id;
        public LUID adapterId;
        // Union payload unused for name queries; keep large enough.
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    private struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)] public int unused;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public int type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME_FLAGS
    {
        public uint value;
    }
}
