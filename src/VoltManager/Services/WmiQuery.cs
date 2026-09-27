using System.Collections.Concurrent;
using System.Management;

namespace VoltManager.Services;

internal readonly record struct BoundedOperationResult<T>(bool Completed, bool TimedOut, T? Value, Exception? Error);

internal interface IBoundedOperationExecutor
{
    BoundedOperationResult<T> Run<T>(Func<T> operation, TimeSpan timeout);
}

internal sealed class ThreadPoolBoundedOperationExecutor : IBoundedOperationExecutor
{
    private readonly SemaphoreSlim _slots;

    public ThreadPoolBoundedOperationExecutor(int maximumConcurrentOperations = 2)
    {
        _slots = new SemaphoreSlim(maximumConcurrentOperations, maximumConcurrentOperations);
    }

    public BoundedOperationResult<T> Run<T>(Func<T> operation, TimeSpan timeout)
    {
        if (!_slots.Wait(0))
            return new BoundedOperationResult<T>(false, true, default, null);

        Task<T> task;
        try
        {
            task = Task.Run(operation);
        }
        catch (Exception ex)
        {
            _slots.Release();
            return new BoundedOperationResult<T>(false, false, default, ex);
        }

        bool completed;
        try
        {
            completed = task.Wait(timeout);
        }
        catch (Exception ex)
        {
            _slots.Release();
            Exception root = ex is AggregateException aggregate ? aggregate.GetBaseException() : ex;
            return new BoundedOperationResult<T>(false, false, default, root);
        }

        if (!completed)
        {
            _ = task.ContinueWith(
                _ => _slots.Release(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return new BoundedOperationResult<T>(false, true, default, null);
        }

        _slots.Release();
        return new BoundedOperationResult<T>(true, false, task.Result, null);
    }
}

internal static class WmiQuery
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarningRepeatInterval = TimeSpan.FromHours(6);
    private static readonly IBoundedOperationExecutor DefaultExecutor = new ThreadPoolBoundedOperationExecutor();
    private static readonly ConcurrentDictionary<string, DateTimeOffset> LastWarnings = new(StringComparer.Ordinal);

    public static IReadOnlyList<T> Read<T>(
        string namespacePath,
        string query,
        Func<ManagementObject, T> projector,
        TimeSpan? timeout = null,
        IBoundedOperationExecutor? executor = null)
    {
        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        string normalizedNamespace = NormalizeNamespace(namespacePath);
        BoundedOperationResult<IReadOnlyList<T>> result = (executor ?? DefaultExecutor).Run(
            () => ReadCore(normalizedNamespace, query, projector, effectiveTimeout),
            effectiveTimeout);

        if (result.Completed && result.Value != null)
            return result.Value;

        if (result.TimedOut)
            WarnRateLimited(normalizedNamespace + "|" + query, $"WMI query timed out after {effectiveTimeout.TotalSeconds:F0}s: {query}");
        else if (result.Error != null)
            WarnRateLimited(normalizedNamespace + "|" + query, $"WMI query failed: {query}: {result.Error.Message}");
        return Array.Empty<T>();
    }

    /// <summary>
    /// Bounded like <see cref="Read{T}"/>, but surfaces failures to the caller: the original
    /// exception is rethrown and a timeout becomes <see cref="TimeoutException"/>. For call
    /// sites whose own catch/log/fallback semantics must be preserved (e.g. WMI writes).
    /// </summary>
    public static IReadOnlyList<T> ReadOrThrow<T>(
        string namespacePath,
        string query,
        Func<ManagementObject, T> projector,
        TimeSpan? timeout = null,
        IBoundedOperationExecutor? executor = null)
    {
        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        string normalizedNamespace = NormalizeNamespace(namespacePath);
        BoundedOperationResult<IReadOnlyList<T>> result = (executor ?? DefaultExecutor).Run(
            () => ReadCore(normalizedNamespace, query, projector, effectiveTimeout),
            effectiveTimeout);

        if (result.Completed && result.Value != null)
            return result.Value;
        if (result.Error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(result.Error).Throw();
        throw new TimeoutException($"WMI query timed out after {effectiveTimeout.TotalSeconds:F0}s: {query}");
    }

    internal static BoundedOperationResult<T> RunBounded<T>(
        Func<T> operation,
        TimeSpan timeout,
        IBoundedOperationExecutor? executor = null)
        => (executor ?? DefaultExecutor).Run(operation, timeout);

    private static IReadOnlyList<T> ReadCore<T>(
        string namespacePath,
        string query,
        Func<ManagementObject, T> projector,
        TimeSpan timeout)
    {
        var connection = new ConnectionOptions { Timeout = timeout };
        var scope = new ManagementScope(namespacePath, connection);
        scope.Connect();
        var options = new EnumerationOptions
        {
            Timeout = timeout,
            ReturnImmediately = true,
            Rewindable = false,
        };
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query), options);
        using ManagementObjectCollection results = searcher.Get();
        var values = new List<T>();
        foreach (ManagementObject obj in results)
        {
            using (obj)
                values.Add(projector(obj));
        }
        return values;
    }

    private static string NormalizeNamespace(string namespacePath)
    {
        if (string.IsNullOrWhiteSpace(namespacePath))
            return @"\\.\root\cimv2";
        if (namespacePath.StartsWith(@"\\", StringComparison.Ordinal))
            return namespacePath;
        return @"\\.\" + namespacePath.TrimStart('\\');
    }

    private static void WarnRateLimited(string key, string message)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (LastWarnings.TryGetValue(key, out DateTimeOffset last) && now - last < WarningRepeatInterval)
            return;
        LastWarnings[key] = now;
        Logger.Warn(message);
    }
}
