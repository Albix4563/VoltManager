using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace VoltManager.Reliability;

public static class AppExitCodes
{
    public const int Success = 0;
    public const int StartupFailure = 1;
    public const int UnhandledUiException = 11;
}

public static class SupervisorBootstrap
{
    private const string SupervisedArgument = "--supervised";
    private const string AppMutexName = "VoltManager_SingleInstance_Mutex";
    private const string SupervisorMutexName = "VoltManager_Supervisor_Mutex";
    private const string SupervisorWakeEventName = "VoltManager_Supervisor_Wake_Event";

    public static bool TryDelegate(string[] arguments)
    {
        if (arguments.Any(argument => string.Equals(argument, SupervisedArgument, StringComparison.OrdinalIgnoreCase)))
            return false;

        string supervisorPath = Path.Combine(AppContext.BaseDirectory, "VoltManager.Supervisor.exe");
        string childPath = Environment.ProcessPath ?? string.Empty;
        if (!File.Exists(supervisorPath) || string.IsNullOrWhiteSpace(childPath))
            return false;

        if (NamedMutexExists(SupervisorMutexName))
        {
            // Let the existing application instance handle show/remote-command forwarding.
            if (NamedMutexExists(AppMutexName))
                return false;

            // The supervisor is between attempts. Wake it instead of starting an unsupervised child.
            return SignalExistingSupervisor();
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = supervisorPath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--child");
            startInfo.ArgumentList.Add(childPath);
            startInfo.ArgumentList.Add("--");
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo)?.Dispose();
            return true;
        }
        catch
        {
            // Fail open: a missing/broken supervisor must not make the application unlaunchable.
            return false;
        }
    }

    private static bool NamedMutexExists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out Mutex? mutex))
                return false;
            mutex.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SignalExistingSupervisor()
    {
        try
        {
            using EventWaitHandle wake = EventWaitHandle.OpenExisting(SupervisorWakeEventName);
            wake.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class CrashDiagnostics
{
    public static string? Capture(string category, Exception? exception, int exitCode)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoltManager",
            "crashes");
        return CaptureToDirectory(directory, category, exception, exitCode);
    }

    public static string? CaptureToDirectory(string directory, string category, Exception? exception, int exitCode)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string[] innerTypes = EnumerateInnerTypes(exception).ToArray();
            var payload = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                category,
                exitCode,
                processId = Environment.ProcessId,
                processUptimeMs = (long)(DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds,
                applicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                osVersion = Environment.OSVersion.VersionString,
                exceptionType = exception?.GetType().FullName,
                exceptionHResult = exception?.HResult,
                exceptionSource = exception?.Source,
                exceptionStackTrace = exception?.StackTrace,
                innerExceptionTypes = innerTypes,
            };

            string fileName = $"crash-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Environment.ProcessId}-{Guid.NewGuid():N}.json";
            string finalPath = Path.Combine(directory, fileName);
            string temporaryPath = finalPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, finalPath);
            return finalPath;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateInnerTypes(Exception? exception)
    {
        int depth = 0;
        for (Exception? current = exception?.InnerException; current != null && depth < 8; current = current.InnerException)
        {
            yield return current.GetType().FullName ?? current.GetType().Name;
            depth++;
        }
    }
}

public sealed record CleanupStep(string Name, Action Action);

public enum CleanupOutcome
{
    Completed,
    TimedOut,
    Failed,
    Skipped,
}

public sealed record CleanupStepResult(string Name, CleanupOutcome Outcome, string? ExceptionType = null);

public static class BoundedCleanup
{
    public static IReadOnlyList<CleanupStepResult> Run(
        IEnumerable<CleanupStep> steps,
        TimeSpan totalTimeout,
        TimeSpan maximumPerStep)
    {
        var results = new List<CleanupStepResult>();
        var stopwatch = Stopwatch.StartNew();

        foreach (CleanupStep step in steps)
        {
            TimeSpan remaining = totalTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                results.Add(new CleanupStepResult(step.Name, CleanupOutcome.Skipped));
                continue;
            }

            TimeSpan stepTimeout = remaining < maximumPerStep ? remaining : maximumPerStep;
            try
            {
                Task task = Task.Run(step.Action);
                if (!task.Wait(stepTimeout))
                {
                    results.Add(new CleanupStepResult(step.Name, CleanupOutcome.TimedOut));
                    continue;
                }

                if (task.Exception != null)
                {
                    string type = task.Exception.GetBaseException().GetType().FullName
                        ?? task.Exception.GetBaseException().GetType().Name;
                    results.Add(new CleanupStepResult(step.Name, CleanupOutcome.Failed, type));
                }
                else
                {
                    results.Add(new CleanupStepResult(step.Name, CleanupOutcome.Completed));
                }
            }
            catch (Exception ex)
            {
                Exception root = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
                results.Add(new CleanupStepResult(
                    step.Name,
                    CleanupOutcome.Failed,
                    root.GetType().FullName ?? root.GetType().Name));
            }
        }

        return results;
    }
}
