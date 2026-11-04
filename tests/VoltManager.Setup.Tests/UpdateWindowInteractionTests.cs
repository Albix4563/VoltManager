using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VoltManager.Setup.Engine;
using VoltManager.Setup.Windows;
using VoltManager.Services;

namespace VoltManager.Setup.Tests
{
    /// <summary>
    /// Exercises the real updater/setup controls on a synthetic STA host: resources are
    /// loaded from App.xaml, windows are created without Show() and no install engine runs.
    /// </summary>
    public sealed class UpdateWindowInteractionTests
    {
        private const int GwlStyle = -16;
        private const uint WsMaximizeBox = 0x00010000;
        private const uint WsMinimizeBox = 0x00020000;
        private const uint MfByCommand = 0x00000000;
        private const uint MfGrayed = 0x00000001;
        private const uint ScClose = 0xF060;
        private const uint ScMaximize = 0xF030;
        private const uint ScMinimize = 0xF020;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr windowHandle, bool revert);

        [DllImport("user32.dll")]
        private static extern uint GetMenuState(IntPtr menuHandle, uint item, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr windowHandle, int index);

        [Fact]
        public void Initial_state_blocks_close_and_keeps_only_minimize()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);

                Assert.Equal(ResizeMode.CanMinimize, window.ResizeMode);
                Assert.True(IsCommandDisabled(handle, ScClose));
                Assert.False(IsCommandDisabled(handle, ScMinimize));
                Assert.True(IsCommandDisabled(handle, ScMaximize));

                uint style = unchecked((uint)GetWindowLong(handle, GwlStyle));
                Assert.Equal(WsMinimizeBox, style & WsMinimizeBox);
                Assert.Equal(0u, style & WsMaximizeBox);

                Assert.Equal(I18n.T("update_minimize_hint"),
                    ((TextBlock)window.FindName("MinimizeHintText")).Text);

                int aborts = 0;
                window.UpdateAborted += (_, __) => aborts++;
                Assert.False(CloseAndReport(window));
                Assert.Equal(0, aborts);
            });
        }

        [Fact]
        public void Waiting_for_proceed_allows_the_cancel_and_reports_one_abort()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);

                window.SetWaitingForProceed();

                Assert.False(IsCommandDisabled(handle, ScClose));
                var close = (Button)window.FindName("CloseButton");
                Assert.Equal(Visibility.Visible, close.Visibility);
                Assert.Equal(I18n.T("btn_cancel"), close.Content);

                int aborts = 0;
                window.UpdateAborted += (_, __) => aborts++;
                Assert.True(CloseAndReport(window));
                Assert.Equal(1, aborts);
            });
        }

        [Fact]
        public void Started_update_relocks_close_and_drops_the_cancel_action()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);

                window.SetWaitingForProceed();
                window.SetUpdateStarted();

                Assert.True(IsCommandDisabled(handle, ScClose));
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("CloseButton")).Visibility);

                int aborts = 0;
                window.UpdateAborted += (_, __) => aborts++;
                Assert.False(CloseAndReport(window));
                Assert.Equal(0, aborts);
            });
        }

        [Fact]
        public void Completed_update_restores_the_expected_exit_actions()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);

                window.SetUpdateStarted();
                window.ShowCompleted();

                Assert.False(IsCommandDisabled(handle, ScClose));
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("CloseButton")).Visibility);

                int aborts = 0;
                window.UpdateAborted += (_, __) => aborts++;
                Assert.True(CloseAndReport(window));
                Assert.Equal(0, aborts);
            });
        }

        [Fact]
        public void Failure_state_shows_the_error_and_allows_exit()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);

                window.SetUpdateStarted();
                window.ShowFailure(new InvalidOperationException("synthetic update failure"));

                Assert.False(IsCommandDisabled(handle, ScClose));
                Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ErrorPanel")).Visibility);
                Assert.Contains("synthetic update failure", ((TextBlock)window.FindName("ErrorText")).Text);
                Assert.Equal(Visibility.Collapsed, ((UIElement)window.FindName("BusyIndicator")).Visibility);

                int aborts = 0;
                window.UpdateAborted += (_, __) => aborts++;
                Assert.True(CloseAndReport(window));
                Assert.Equal(0, aborts);
            });
        }

        [Fact]
        public void Retry_relocks_close_while_the_relaunch_is_pending()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);
                var pending = new TaskCompletionSource<bool>();

                window.ShowFailure(new InvalidOperationException("synthetic relaunch failure"), () => pending.Task);
                Assert.False(IsCommandDisabled(handle, ScClose));

                ((Button)window.FindName("RetryLaunchButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.True(IsCommandDisabled(handle, ScClose));
                Assert.False(CloseAndReport(window));
            });
        }

        [Fact]
        public void Finished_retry_unlocks_the_exit_actions_again()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                IntPtr handle = Handle(window);

                window.ShowFailure(new InvalidOperationException("synthetic relaunch failure"),
                    () => Task.FromResult(true));

                ((Button)window.FindName("RetryLaunchButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.False(IsCommandDisabled(handle, ScClose));
                Assert.True(((Button)window.FindName("CloseButton")).IsEnabled);
                Assert.True(CloseAndReport(window));
            });
        }

        [Fact]
        public void Host_snapshot_palette_wins_over_the_restyle_defaults()
        {
            WpfSetupHost.Run(() =>
            {
                string[] dark = {
                    "#0B1120", "#111827", "#1E293B", "#123456", "#567890",
                    "#ABCDEF", "#F8FAFC", "#CBD5E1", "#434343", "#FFFFFF"
                };
                ApplyHostPalette(dark);

                UpdateWindow darkWindow = CreateWindow();
                Assert.Equal(Color.FromRgb(0x12, 0x34, 0x56), ColorOf(ResourceBrush("AccentBrush")));
                Assert.Equal(Color.FromRgb(0xF8, 0xFA, 0xFC),
                    ColorOf(((TextBlock)darkWindow.FindName("HeadingText")).Foreground));
                Assert.Equal(Color.FromRgb(0x0B, 0x11, 0x20), ColorOf(darkWindow.Background));
                Assert.Equal(Color.FromRgb(0x11, 0x18, 0x27),
                    ColorOf(((Border)darkWindow.FindName("Footer")).Background));
                Assert.Equal(Color.FromRgb(0x1E, 0x29, 0x3B), ColorOf(ResourceBrush("InsetBrush")));

                string[] light = {
                    "#FFFFFF", "#F1F5F9", "#E2E8F0", "#1D4ED8", "#3B82F6",
                    "#1E40AF", "#0F172A", "#334155", "#CBD5E1", "#000000"
                };
                ApplyHostPalette(light);

                UpdateWindow lightWindow = CreateWindow();
                Assert.Equal(Color.FromRgb(0x1D, 0x4E, 0xD8), ColorOf(ResourceBrush("AccentBrush")));
                Assert.Equal(Color.FromRgb(0x0F, 0x17, 0x2A),
                    ColorOf(((TextBlock)lightWindow.FindName("HeadingText")).Foreground));
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), ColorOf(lightWindow.Background));
                Assert.Equal(Color.FromRgb(0xF1, 0xF5, 0xF9),
                    ColorOf(((Border)lightWindow.FindName("Footer")).Background));
                Assert.Equal(Color.FromRgb(0xE2, 0xE8, 0xF0), ColorOf(ResourceBrush("InsetBrush")));
            });
        }

        [Fact]
        public void Height_limited_content_scrolls_without_clipping_the_footer()
        {
            WpfSetupHost.Run(() =>
            {
                UpdateWindow window = CreateWindow();
                window.ShowFailure(new InvalidOperationException(new string('x', 600)),
                    () => Task.CompletedTask);
                var root = (Grid)window.Content;
                var size = new Size(520, 280);
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                var footer = (Border)window.FindName("Footer");
                var scroll = (ScrollViewer)window.FindName("ContentScroll");
                Assert.True(scroll.ScrollableHeight > 0);
                Assert.True(footer.ActualHeight > 0);
                Assert.True(footer.TranslatePoint(new Point(0, footer.ActualHeight), root).Y <= size.Height + 1);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("CloseButton")).Visibility);
                Assert.Equal(Visibility.Visible, ((Button)window.FindName("RetryLaunchButton")).Visibility);
                window.Close();
            });
        }

        [Fact]
        public void Setup_window_close_control_follows_the_operation_guard()
        {
            WpfSetupHost.Run(() =>
            {
                var window = new SetupWindow(SetupArgs.Parse(Array.Empty<string>()));
                var close = (Button)window.FindName("BtnClose");
                Assert.True(close.IsEnabled);

                MethodInfo setRunning = typeof(SetupWindow).GetMethod(
                    "SetOperationRunning", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(setRunning);

                setRunning!.Invoke(window, new object[] { true });
                Assert.False(close.IsEnabled);

                setRunning.Invoke(window, new object[] { false });
                Assert.True(close.IsEnabled);
                Assert.True(CloseAndReport(window));
            });
        }

        [Fact]
        public void Progress_and_result_pages_load_with_the_shared_resources()
        {
            WpfSetupHost.Run(() =>
            {
                var progress = new VoltManager.Setup.Pages.ProgressPage();
                progress.SetStatus("Synthetic step", 42);
                Assert.Equal(42, ((ProgressBar)progress.FindName("Bar")).Value);
                Assert.Equal("42%", ((TextBlock)progress.FindName("PctText")).Text);
                Assert.Equal("Synthetic step", ((TextBlock)progress.FindName("StatusText")).Text);

                var warning = new VoltManager.Setup.Pages.DonePage(
                    null, true, null, uninstall: true, warningMsg: "synthetic note");
                Assert.Equal(Visibility.Visible, ((FrameworkElement)warning.FindName("ErrCard")).Visibility);

                var failure = new VoltManager.Setup.Pages.DonePage(null, false, "synthetic failure");
                Assert.Equal(Visibility.Visible, ((FrameworkElement)failure.FindName("ErrCard")).Visibility);
                Assert.Equal(I18n.T("done_title_err"), ((TextBlock)failure.FindName("TitleText")).Text);
            });
        }

        private static UpdateWindow CreateWindow()
        {
            var window = new UpdateWindow();
            window.SetVersion("9.9.9");
            return window;
        }

        private static IntPtr Handle(Window window)
            => new WindowInteropHelper(window).EnsureHandle();

        private static bool CloseAndReport(Window window)
        {
            bool closed = false;
            EventHandler onClosed = (_, __) => closed = true;
            window.Closed += onClosed;
            window.Close();
            window.Closed -= onClosed;
            return closed;
        }

        private static bool IsCommandDisabled(IntPtr windowHandle, uint command)
        {
            IntPtr menu = GetSystemMenu(windowHandle, false);
            Assert.NotEqual(IntPtr.Zero, menu);
            uint state = GetMenuState(menu, command, MfByCommand);
            Assert.NotEqual(uint.MaxValue, state);
            return (state & MfGrayed) != 0;
        }

        private static void ApplyHostPalette(string[] colors)
        {
            MethodInfo apply = typeof(App).GetMethod(
                "ApplyUpdatePalette", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(apply);
            apply!.Invoke(Application.Current, new object[] { UpdateSessionProtocol.EncodePalette(colors) });
        }

        private static Brush ResourceBrush(string key)
            => (Brush)Application.Current.Resources[key];

        private static Color ColorOf(Brush brush)
            => ((SolidColorBrush)brush).Color;
    }

    /// <summary>
    /// Single STA host for the setup UI tests: one Application per AppDomain, and every
    /// action runs on its dispatcher so failures surface on the calling xUnit thread.
    /// </summary>
    internal static class WpfSetupHost
    {
        private static readonly object Gate = new object();
        private static Dispatcher? _dispatcher;
        private static Exception? _startupFailure;

        public static void Run(Action action)
        {
            Dispatcher dispatcher = GetDispatcher();
            Exception? failure = null;
            dispatcher.Invoke(new Action(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }));
            if (failure != null)
                ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static Dispatcher GetDispatcher()
        {
            lock (Gate)
            {
                if (_dispatcher != null) return _dispatcher;
                if (_startupFailure != null) ExceptionDispatchInfo.Capture(_startupFailure).Throw();

                using var ready = new ManualResetEventSlim(false);
                var thread = new Thread(() =>
                {
                    try
                    {
                        var application = new App();
                        application.InitializeComponent();
                        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        I18n.Initialize("en");
                        _dispatcher = Dispatcher.CurrentDispatcher;
                    }
                    catch (Exception ex)
                    {
                        _startupFailure = ex;
                    }
                    finally
                    {
                        ready.Set();
                    }

                    if (_startupFailure == null)
                        Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "VoltManager setup UI tests"
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                ready.Wait(TimeSpan.FromSeconds(30));
                if (_startupFailure != null) ExceptionDispatchInfo.Capture(_startupFailure).Throw();
                if (_dispatcher == null)
                    throw new InvalidOperationException("The WPF test host did not start.");
                return _dispatcher;
            }
        }
    }
}
