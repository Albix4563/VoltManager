using System.Windows;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class MainWindowStatePolicyTests
{
    [Theory]
    [InlineData(false, false, WindowState.Normal)]
    [InlineData(false, true, WindowState.Maximized)]
    [InlineData(true, false, WindowState.Minimized)]
    [InlineData(true, true, WindowState.Minimized)]
    public void GetInitialState_resolves_startup_window_state(
        bool startMinimized,
        bool startMaximized,
        WindowState expected)
    {
        WindowState actual = MainWindowStatePolicy.GetInitialState(startMinimized, startMaximized);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(false, WindowState.Normal)]
    [InlineData(true, WindowState.Maximized)]
    public void GetRestoreFromTrayState_first_open_applies_start_maximized_preference(
        bool startMaximized,
        WindowState expected)
    {
        WindowState actual = MainWindowStatePolicy.GetRestoreFromTrayState(
            hasBeenShown: false,
            startMaximized: startMaximized,
            lastVisibleState: WindowState.Normal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetRestoreFromTrayState_subsequent_opens_preserve_last_visible_state()
    {
        Assert.Equal(
            WindowState.Maximized,
            MainWindowStatePolicy.GetRestoreFromTrayState(
                hasBeenShown: true,
                startMaximized: false,
                lastVisibleState: WindowState.Maximized));

        Assert.Equal(
            WindowState.Normal,
            MainWindowStatePolicy.GetRestoreFromTrayState(
                hasBeenShown: true,
                startMaximized: true,
                lastVisibleState: WindowState.Normal));

        Assert.Equal(
            WindowState.Normal,
            MainWindowStatePolicy.GetRestoreFromTrayState(
                hasBeenShown: true,
                startMaximized: true,
                lastVisibleState: WindowState.Minimized));
    }

    [Fact]
    public void RememberVisibleState_ignores_minimized_and_tracks_normal_or_maximized()
    {
        Assert.Equal(
            WindowState.Maximized,
            MainWindowStatePolicy.RememberVisibleState(WindowState.Maximized, WindowState.Normal));

        Assert.Equal(
            WindowState.Maximized,
            MainWindowStatePolicy.RememberVisibleState(WindowState.Minimized, WindowState.Maximized));

        Assert.Equal(
            WindowState.Normal,
            MainWindowStatePolicy.RememberVisibleState(WindowState.Normal, WindowState.Maximized));

        Assert.Equal(
            WindowState.Normal,
            MainWindowStatePolicy.RememberVisibleState(WindowState.Minimized, WindowState.Minimized));
    }
}
