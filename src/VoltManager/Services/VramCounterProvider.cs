using System.Diagnostics;
using System.Runtime.InteropServices;
using VoltManager.Models;

namespace VoltManager.Services;

public sealed class VramCounterProvider : IDisposable
{
    internal readonly record struct GpuLuid(uint LowPart, int HighPart)
    {
        public override string ToString() => $"0x{unchecked((uint)HighPart):X8}:0x{LowPart:X8}";
    }

    internal readonly record struct UsageSample(GpuLuid Luid, string Instance, long UsedBytes);

    private const long SignificantDedicatedMemoryBytes = 256L * 1024 * 1024;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CounterRefreshInterval = TimeSpan.FromSeconds(30);
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    private readonly object _gate = new();
    private Dictionary<string, PerformanceCounter> _counters = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<GpuLuid, long> _capacities = new Dictionary<GpuLuid, long>();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private VramMemorySnapshot _last = new();
    private volatile bool _ready;
    private bool _disposed;
    private bool _refreshFaulted;
    private bool _readFaulted;

    public VramCounterProvider() : this(initialize: true) { }

    internal VramCounterProvider(bool initialize)
    {
        if (initialize) Task.Run(Initialize);
    }

    private void Initialize()
    {
        lock (_gate)
        {
            if (_disposed) return;
            RefreshIfDue(DateTime.UtcNow, RefreshLocked);
            _ready = true;
        }
    }

    public VramMemorySnapshot Read(bool force = false)
    {
        if (!_ready) return new VramMemorySnapshot();
        lock (_gate)
        {
            if (_disposed) return new VramMemorySnapshot();
            DateTime now = DateTime.UtcNow;
            if (!force && _lastSampleUtc != DateTime.MinValue && now - _lastSampleUtc < SampleInterval)
                return _last;
            ValidationMetrics.Increment(ValidationCounter.VramSamples);

            RefreshIfDue(now, RefreshLocked);

            var usage = new List<UsageSample>(_counters.Count);
            bool readFailed = false;
            foreach (var pair in _counters)
            {
                if (!TryParseLuid(pair.Key, out var luid)) continue;
                try
                {
                    long bytes = Math.Max(0, pair.Value.NextSample().RawValue);
                    usage.Add(new UsageSample(luid, pair.Key, bytes));
                }
                catch (Exception ex)
                {
                    readFailed = true;
                    _readFaulted = Logger.WarnOnce(_readFaulted, "VRAM counter read failed", ex);
                }
            }
            if (!readFailed) _readFaulted = false;

            _lastSampleUtc = now;
            _last = BuildSnapshot(usage, _capacities, now);
            return _last;
        }
    }

    // Caller holds _gate. Failed discovery is throttled too, even for forced foreground reads.
    internal void RefreshIfDue(DateTime now, Action refresh)
    {
        if (_lastRefreshUtc != DateTime.MinValue && now - _lastRefreshUtc < CounterRefreshInterval) return;
        _lastRefreshUtc = now;
        try
        {
            refresh();
            _refreshFaulted = false;
        }
        catch (Exception ex)
        {
            _capacities = new Dictionary<GpuLuid, long>();
            _refreshFaulted = Logger.WarnOnce(_refreshFaulted, "VRAM counters unavailable", ex);
        }
    }

    private void RefreshLocked()
    {
        var category = new PerformanceCounterCategory("GPU Adapter Memory");
        var names = category.GetInstanceNames()
            .Where(name => TryParseLuid(name, out _))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string removed in _counters.Keys.Where(name => !names.Contains(name)).ToArray())
        {
            _counters[removed].Dispose();
            _counters.Remove(removed);
        }

        foreach (string added in names.Where(name => !_counters.ContainsKey(name)))
            _counters[added] = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", added, readOnly: true);

        _capacities = ReadDxgiCapacities();
    }

    internal static VramMemorySnapshot BuildSnapshot(
        IReadOnlyCollection<UsageSample> usage,
        IReadOnlyDictionary<GpuLuid, long> capacities,
        DateTime timestampUtc)
    {
        var duplicated = usage.GroupBy(sample => sample.Luid)
            .Where(group => group.Count() != 1)
            .Select(group => group.Key)
            .ToHashSet();
        var adapters = new List<VramAdapterMemorySample>();

        foreach (var sample in usage)
        {
            if (duplicated.Contains(sample.Luid)) continue;
            if (!capacities.TryGetValue(sample.Luid, out long capacity) || capacity < SignificantDedicatedMemoryBytes)
                continue;

            double pressure = Math.Clamp(sample.UsedBytes / (double)capacity * 100, 0, 100);
            adapters.Add(new VramAdapterMemorySample
            {
                Adapter = sample.Luid.ToString(),
                UsedBytes = sample.UsedBytes,
                CapacityBytes = capacity,
                PressurePercent = Math.Round(pressure, 1),
                TimestampUtc = timestampUtc,
            });
        }

        if (adapters.Count == 0) return new VramMemorySnapshot();
        return new VramMemorySnapshot
        {
            Available = true,
            PressurePercent = adapters.Max(adapter => adapter.PressurePercent),
            TimestampUtc = timestampUtc,
            Adapters = adapters,
        };
    }

    internal static bool TryParseLuid(string instance, out GpuLuid luid)
    {
        luid = default;
        if (string.IsNullOrWhiteSpace(instance)) return false;
        int marker = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        string[] parts = instance[(marker + 5)..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !TryHex(parts[0], out uint high) || !TryHex(parts[1], out uint low)) return false;
        luid = new GpuLuid(low, unchecked((int)high));
        return true;
    }

    private static bool TryHex(string value, out uint result)
    {
        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static IReadOnlyDictionary<GpuLuid, long> ReadDxgiCapacities()
    {
        Guid iid = new("770AAE78-F26F-4DBA-A829-253C83D1B387");
        int hr = CreateDXGIFactory1(ref iid, out IntPtr factory);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        var result = new Dictionary<GpuLuid, long>();
        var ambiguous = new HashSet<GpuLuid>();
        try
        {
            var enumAdapters = VtableDelegate<EnumAdapters1Delegate>(factory, 12);
            for (uint index = 0; ; index++)
            {
                hr = enumAdapters(factory, index, out IntPtr adapter);
                if (hr == DxgiErrorNotFound) break;
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                try
                {
                    var getDesc = VtableDelegate<GetDesc1Delegate>(adapter, 10);
                    hr = getDesc(adapter, out DXGI_ADAPTER_DESC1 desc);
                    if (hr < 0) continue;
                    var luid = new GpuLuid(desc.AdapterLuid.LowPart, desc.AdapterLuid.HighPart);
                    long capacity = checked((long)desc.DedicatedVideoMemory);
                    if (!result.TryAdd(luid, capacity)) ambiguous.Add(luid);
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }

        foreach (var luid in ambiguous) result.Remove(luid);
        return result;
    }

    private static T VtableDelegate<T>(IntPtr instance, int index) where T : Delegate
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        IntPtr method = Marshal.ReadIntPtr(table, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(method);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var counter in _counters.Values) counter.Dispose();
            _counters.Clear();
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumAdapters1Delegate(IntPtr self, uint adapter, out IntPtr ppAdapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int GetDesc1Delegate(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }
}
