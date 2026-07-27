namespace VoltManager.Reliability;

/// <summary>
/// Outcome for an unhandled UI-thread exception. Exactly one policy is active
/// application-wide — never both keep-alive MessageBox and fatal shutdown.
/// </summary>
public enum UnhandledUiAction
{
    /// <summary>Log, optionally notify the user, leave the process running.</summary>
    RecoverKeepAlive,

    /// <summary>Log, capture crash diagnostic, bounded fatal shutdown (exit code 11).</summary>
    FatalShutdownWithDiagnostic,
}

/// <summary>
/// Single source of truth for how unhandled exceptions are handled.
/// Wired by <c>App.Reliability</c>; legacy keep-alive handlers must not re-register.
/// </summary>
public static class UnhandledExceptionPolicy
{
    /// <summary>
    /// UI-thread unhandled exceptions are fatal: a corrupted dispatcher path is
    /// not safe to keep serving the tray UI. The external supervisor restarts
    /// the process on exit code <see cref="AppExitCodes.UnhandledUiException"/>.
    /// </summary>
    public static UnhandledUiAction UiThreadPolicy { get; } = UnhandledUiAction.FatalShutdownWithDiagnostic;

    public static bool KeepsProcessAlive(UnhandledUiAction action)
        => action == UnhandledUiAction.RecoverKeepAlive;

    public static bool CapturesCrashDiagnostic(UnhandledUiAction action)
        => action == UnhandledUiAction.FatalShutdownWithDiagnostic;

    public static bool BeginsFatalShutdown(UnhandledUiAction action)
        => action == UnhandledUiAction.FatalShutdownWithDiagnostic;
}
