namespace VoltManager.Services.GameDetection;

public sealed class ProcessGraph
{
    private readonly IReadOnlyDictionary<int, ProcessSample> _processes;

    public ProcessGraph(IEnumerable<ProcessSample> processes)
    {
        _processes = processes
            .GroupBy(process => process.Pid)
            .ToDictionary(group => group.Key, group => group.First());
    }

    public IReadOnlyList<ProcessSample> GetAncestors(int processId, int maxDepth)
    {
        if (maxDepth <= 0 || !_processes.TryGetValue(processId, out var current))
            return Array.Empty<ProcessSample>();

        var ancestors = new List<ProcessSample>(maxDepth);
        var visited = new HashSet<int> { processId };

        for (int depth = 0; depth < maxDepth; depth++)
        {
            int parentPid = current.ParentPid;
            if (parentPid <= 0 || !visited.Add(parentPid) ||
                !_processes.TryGetValue(parentPid, out var parent) || !IsValidParent(current, parent))
                break;

            current = parent;
            ancestors.Add(current);
        }

        return ancestors;
    }

    public bool TryFindAncestor(
        int processId,
        Func<ProcessSample, bool> predicate,
        int maxDepth,
        out ProcessSample ancestor)
    {
        if (maxDepth <= 0 || !_processes.TryGetValue(processId, out var current))
        {
            ancestor = default;
            return false;
        }

        var visited = new HashSet<int> { processId };
        for (int depth = 0; depth < maxDepth; depth++)
        {
            int parentPid = current.ParentPid;
            if (parentPid <= 0 || !visited.Add(parentPid) ||
                !_processes.TryGetValue(parentPid, out var parent) || !IsValidParent(current, parent))
                break;

            current = parent;
            if (predicate(current))
            {
                ancestor = current;
                return true;
            }
        }

        ancestor = default;
        return false;
    }

    private static bool IsValidParent(in ProcessSample child, in ProcessSample parent)
        => child.StartTimeUtc == null || parent.StartTimeUtc == null || parent.StartTimeUtc <= child.StartTimeUtc;
}
