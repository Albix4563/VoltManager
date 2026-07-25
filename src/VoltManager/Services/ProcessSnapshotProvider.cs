using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoltManager.Services;

/// <summary>One process as seen by a snapshot. No handle is kept open.</summary>
public readonly record struct ProcessSample(
    int Pid,
    int ParentPid,
    string Name,
    long WorkingSetBytes,
    TimeSpan CpuTime,
    DateTime? StartTimeUtc);

/// <summary>Every process alive at <see cref="TakenUtc"/>. Treat the array as read-only.</summary>
public sealed class ProcessSnapshot
{
    public static readonly ProcessSnapshot Empty = new(DateTime.MinValue, Array.Empty<ProcessSample>());

    public DateTime TakenUtc { get; }
    public ProcessSample[] Processes { get; }

    internal ProcessSnapshot(DateTime takenUtc, ProcessSample[] processes)
    {
        TakenUtc = takenUtc;
        Processes = processes;
    }
}

/// <summary>
/// One system-wide process enumeration shared by every scanner.
///
/// Each scanner used to call <c>Process.GetProcesses()</c> on its own timer and then
/// resolve paths through <c>Process.MainModule</c> — which enumerates *every* module of
/// *every* process and throws on protected ones. Three loops × a few hundred Process
/// objects × thousands of module records and caught exceptions, every few seconds, was
/// the app's dominant source of garbage and therefore of managed-heap growth.
///
/// A single NtQuerySystemInformation call into a reused pinned buffer yields pid, name,
/// working set, CPU time and start time for the whole system without opening a handle.
/// Image paths are resolved lazily and cached per (pid, start time), so a long-running
/// process is resolved once instead of on every scan.
/// </summary>
public static class ProcessSnapshotProvider
{
    private const int SystemProcessInformation = 5;
    private const uint StatusSuccess = 0x00000000;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint StatusBufferTooSmall = 0xC0000023;

    // x64 offsets of SYSTEM_PROCESS_INFORMATION. The app is win-x64 only; the
    // managed fallback covers any other bitness.
    private const int OffNextEntry = 0x00;
    private const int OffCreateTime = 0x20;
    private const int OffUserTime = 0x28;
    private const int OffKernelTime = 0x30;
    private const int OffImageNameLength = 0x38;
    private const int OffImageNameBuffer = 0x40;
    private const int OffUniqueProcessId = 0x50;
    private const int OffInheritedFromUniqueProcessId = 0x58;
    private const int OffWorkingSetSize = 0x90;
    private const int EntryMinSize = 0x98;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    private static readonly object Gate = new();
    // Pinned once (POH) so the address stays valid and repeated large-buffer
    // allocations never reach the LOH.
    private static byte[] _buffer = GC.AllocateUninitializedArray<byte>(256 * 1024, pinned: true);
    private static ProcessSnapshot _current = ProcessSnapshot.Empty;
    private static readonly Dictionary<int, ProcessIdentity> Identities = new();
    private static int _generation;
    private static bool _nativeFaulted;

    /// <summary>Per-pid cached identity: survives across snapshots while the pid keeps its start time.</summary>
    private sealed class ProcessIdentity
    {
        public long StartTicks;
        public string Name = "";
        public string? Path; // null = not resolved yet; "" = resolution denied, don't retry
        public int Seen;
    }

    /// <summary>
    /// Returns the shared snapshot, re-capturing only if the cached one is older than
    /// <paramref name="maxAge"/>. Scanners on nearby cadences share one capture.
    /// </summary>
    public static ProcessSnapshot Get(TimeSpan maxAge)
    {
        var cached = Volatile.Read(ref _current);
        if (DateTime.UtcNow - cached.TakenUtc <= maxAge) return cached;

        lock (Gate)
        {
            if (DateTime.UtcNow - _current.TakenUtc <= maxAge) return _current;
            var refreshed = Capture();
            Volatile.Write(ref _current, refreshed);
            return refreshed;
        }
    }

    /// <summary>
    /// Full image path for a sampled process, cached per (pid, start time). Denied
    /// processes cache an empty string so a protected pid is probed once, not every scan.
    /// </summary>
    public static string GetPath(in ProcessSample sample)
    {
        long startTicks = sample.StartTimeUtc?.Ticks ?? 0;

        lock (Gate)
        {
            if (Identities.TryGetValue(sample.Pid, out var known) &&
                known.StartTicks == startTicks && known.Path != null)
                return known.Path;
        }

        // Resolved outside the lock: OpenProcess can block on a busy machine.
        string path = ProcessPathResolver.TryQueryFullProcessImageName(sample.Pid);

        lock (Gate)
        {
            if (Identities.TryGetValue(sample.Pid, out var entry) && entry.StartTicks == startTicks)
                entry.Path = path;
        }
        return path;
    }

    private static ProcessSnapshot Capture()
    {
        var samples = CaptureNative() ?? CaptureManaged();
        var snapshot = new ProcessSnapshot(DateTime.UtcNow, samples);
        PruneIdentities(samples.Length);
        return snapshot;
    }

    private static ProcessSample[]? CaptureNative()
    {
        if (IntPtr.Size != 8) return null;

        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                var ptr = Marshal.UnsafeAddrOfPinnedArrayElement(_buffer, 0);
                uint status = NtQuerySystemInformation(
                    SystemProcessInformation, ptr, _buffer.Length, out int needed);

                if (status == StatusSuccess)
                {
                    _nativeFaulted = false;
                    return Parse(ptr);
                }

                if (status != StatusInfoLengthMismatch && status != StatusBufferTooSmall)
                {
                    _nativeFaulted = Logger.WarnOnce(_nativeFaulted,
                        $"NtQuerySystemInformation failed (0x{status:X8}); using managed enumeration");
                    return null;
                }

                // The process list grew between sizing and reading: grow generously
                // so a busy machine does not ping-pong through this loop.
                int wanted = Math.Max(needed + (64 * 1024), _buffer.Length * 2);
                _buffer = GC.AllocateUninitializedArray<byte>(wanted, pinned: true);
            }

            _nativeFaulted = Logger.WarnOnce(_nativeFaulted,
                "Process buffer kept growing; using managed enumeration");
            return null;
        }
        catch (Exception ex)
        {
            _nativeFaulted = Logger.WarnOnce(_nativeFaulted, "Native process enumeration failed", ex);
            return null;
        }
    }

    private static ProcessSample[] Parse(IntPtr basePtr)
    {
        int generation = ++_generation;
        var buffer = _buffer;
        var samples = new List<ProcessSample>(Identities.Count > 0 ? Identities.Count : 256);

        int offset = 0;
        while (offset >= 0 && offset + EntryMinSize <= buffer.Length)
        {
            long pidRaw = BitConverter.ToInt64(buffer, offset + OffUniqueProcessId);
            long parentRaw = BitConverter.ToInt64(buffer, offset + OffInheritedFromUniqueProcessId);
            long createTime = BitConverter.ToInt64(buffer, offset + OffCreateTime);
            long userTime = BitConverter.ToInt64(buffer, offset + OffUserTime);
            long kernelTime = BitConverter.ToInt64(buffer, offset + OffKernelTime);
            long workingSet = BitConverter.ToInt64(buffer, offset + OffWorkingSetSize);

            int pid = unchecked((int)pidRaw);
            DateTime? startedUtc = null;
            long startTicks = 0;
            if (createTime > 0)
            {
                try
                {
                    var started = DateTime.FromFileTimeUtc(createTime);
                    startedUtc = started;
                    startTicks = started.Ticks;
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Bogus creation stamp (seen on pid 4 in some VMs): treat as unknown.
                }
            }

            string name = ResolveName(basePtr, buffer, offset, pid, startTicks, generation);

            samples.Add(new ProcessSample(
                pid,
                unchecked((int)parentRaw),
                name,
                workingSet < 0 ? 0 : workingSet,
                TimeSpan.FromTicks(Math.Max(0, userTime) + Math.Max(0, kernelTime)),
                startedUtc));

            uint next = BitConverter.ToUInt32(buffer, offset + OffNextEntry);
            if (next == 0) break;
            long advanced = (long)offset + next;
            if (advanced <= offset || advanced > buffer.Length) break;
            offset = (int)advanced;
        }

        return samples.ToArray();
    }

    /// <summary>
    /// Reuses the cached name string when the pid still has the same start time, so a
    /// steady system allocates no strings at all across scans.
    /// </summary>
    private static string ResolveName(IntPtr basePtr, byte[] buffer, int offset, int pid, long startTicks, int generation)
    {
        if (Identities.TryGetValue(pid, out var known) && known.StartTicks == startTicks)
        {
            known.Seen = generation;
            return known.Name;
        }

        string name = ReadImageName(basePtr, buffer, offset, pid);
        Identities[pid] = new ProcessIdentity
        {
            StartTicks = startTicks,
            Name = name,
            Path = null,
            Seen = generation,
        };
        return name;
    }

    /// <summary>UNICODE_STRING → short name, matching Process.ProcessName (no path, no ".exe").</summary>
    private static string ReadImageName(IntPtr basePtr, byte[] buffer, int offset, int pid)
    {
        ushort byteLength = BitConverter.ToUInt16(buffer, offset + OffImageNameLength);
        long stringPtr = BitConverter.ToInt64(buffer, offset + OffImageNameBuffer);
        // The kernel reports no image for the idle process; Process.ProcessName says "Idle".
        if (byteLength == 0 || stringPtr == 0) return pid == 0 ? "Idle" : "";

        // The kernel writes the characters inside our own buffer; bail out if the
        // pointer somehow falls outside it rather than reading foreign memory.
        long relative = stringPtr - basePtr.ToInt64();
        if (relative < 0 || relative + byteLength > buffer.Length) return "";

        string raw = Marshal.PtrToStringUni(new IntPtr(stringPtr), byteLength / 2) ?? "";
        int slash = raw.LastIndexOf('\\');
        if (slash >= 0) raw = raw[(slash + 1)..];
        return raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? raw[..^4] : raw;
    }

    /// <summary>Fallback used only if the native query is unavailable; same data, higher cost.</summary>
    private static ProcessSample[] CaptureManaged()
    {
        int generation = ++_generation;
        var processes = Process.GetProcesses();
        var samples = new List<ProcessSample>(processes.Length);

        foreach (var process in processes)
        {
            try
            {
                DateTime? started = null;
                try { started = process.StartTime.ToUniversalTime(); } catch { }
                TimeSpan cpu = TimeSpan.Zero;
                try { cpu = process.TotalProcessorTime; } catch { }

                long startTicks = started?.Ticks ?? 0;
                if (!Identities.TryGetValue(process.Id, out var known) || known.StartTicks != startTicks)
                {
                    known = new ProcessIdentity { StartTicks = startTicks, Name = process.ProcessName };
                    Identities[process.Id] = known;
                }
                known.Seen = generation;

                // The managed API exposes no parent pid; callers that need the tree
                // only run on the native path.
                samples.Add(new ProcessSample(process.Id, 0, known.Name, process.WorkingSet64, cpu, started));
            }
            catch
            {
                // Process exited between enumeration and read; skip it.
            }
            finally
            {
                process.Dispose();
            }
        }

        return samples.ToArray();
    }

    /// <summary>Mark-and-sweep of dead pids. Only runs once the table has actually drifted.</summary>
    private static void PruneIdentities(int liveCount)
    {
        if (Identities.Count <= liveCount + 64) return;

        int generation = _generation;
        var stale = new List<int>();
        foreach (var pair in Identities)
            if (pair.Value.Seen != generation) stale.Add(pair.Key);

        foreach (int pid in stale) Identities.Remove(pid);
    }
}
