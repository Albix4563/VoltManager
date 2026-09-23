namespace VoltManager.Services;

/// <summary>Distinguishes an actual dashboard load failure from a canceled navigation.</summary>
internal sealed class DashboardNavigationGuard
{
    private readonly HashSet<ulong> _expectedCancellations = new();
    public bool IsAppNavigationPending { get; private set; }

    public void BeginAppNavigation() => IsAppNavigationPending = true;

    public void EndAppNavigation() => IsAppNavigationPending = false;

    public bool CanStartAppNavigation(bool webViewReady, string? source)
        => webViewReady && !IsAppNavigationPending &&
           (string.IsNullOrEmpty(source) || source.StartsWith("about:", StringComparison.OrdinalIgnoreCase));

    public void ExpectCancellation(ulong navigationId)
        => _expectedCancellations.Add(navigationId);

    public bool IsFailure(ulong navigationId, bool isSuccess, bool operationCanceled = false)
    {
        bool expectedCancellation = _expectedCancellations.Remove(navigationId);
        return !isSuccess && !expectedCancellation && !operationCanceled;
    }
}
