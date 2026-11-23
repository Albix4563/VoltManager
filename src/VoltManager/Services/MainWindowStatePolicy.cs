using System.Windows;

namespace VoltManager.Services;

/// <summary>
/// Pure window-state policy for initial launch and restore from the notification area.
/// </summary>
internal static class MainWindowStatePolicy
{
    public static WindowState GetInitialState(bool startMinimized, bool startMaximized)
    {
        if (startMinimized)
            return WindowState.Minimized;

        return startMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    public static WindowState RememberVisibleState(WindowState currentState, WindowState previousVisibleState)
    {
        return currentState == WindowState.Minimized
            ? NormalizeRestoreState(previousVisibleState)
            : NormalizeRestoreState(currentState);
    }

    public static WindowState GetRestoreFromTrayState(
        bool hasBeenShown,
        bool startMaximized,
        WindowState lastVisibleState)
    {
        if (!hasBeenShown)
            return startMaximized ? WindowState.Maximized : WindowState.Normal;

        return NormalizeRestoreState(lastVisibleState);
    }

    private static WindowState NormalizeRestoreState(WindowState state)
        => state == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
}
