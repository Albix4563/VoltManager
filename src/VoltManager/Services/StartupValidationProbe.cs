using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VoltManager.Services;

/// <summary>Monotonic, per-process startup markers for isolated validation builds.</summary>
internal static class StartupValidationProbe
{
    private static readonly object Gate = new();
#if VOLTMANAGER_VALIDATION_HOOKS
    private sealed record Marker(int Pid, long Ticks, string Stage, string Folder);
#endif

    [Conditional("VOLTMANAGER_VALIDATION_HOOKS")]
    internal static void Mark(string stage)
    {
#if VOLTMANAGER_VALIDATION_HOOKS
        if (!ValidationEnvironment.IsActive) return;
        long ticks = Stopwatch.GetTimestamp();
        var marker = new Marker(Environment.ProcessId, ticks, stage,
            Path.Combine(ValidationEnvironment.ApplicationDataRoot, "VoltManager"));
        // Capture the timestamp on the caller; file I/O must never stall WPF's dispatcher.
        ThreadPool.QueueUserWorkItem(static marker =>
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(marker.Folder);
                    File.AppendAllText(Path.Combine(marker.Folder, "startup-events.jsonl"),
                        JsonSerializer.Serialize(new { pid = marker.Pid, ticks = marker.Ticks, stage = marker.Stage }) + Environment.NewLine);
                }
            }
            catch (IOException) { /* Harness treats a missing marker as an unsuccessful run. */ }
            catch (UnauthorizedAccessException) { /* Validation must never break production startup. */ }
        }, marker, preferLocal: false);
#endif
    }
}
