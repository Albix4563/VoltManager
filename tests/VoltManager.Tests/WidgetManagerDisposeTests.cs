using System.Reflection;
using VoltManager.Localization;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WidgetManagerDisposeTests
{
    [Fact]
    public void Dispose_unsubscribes_settings_and_theme_handlers_and_is_idempotent()
    {
        var settings = TestSettings.Create();
        var theme = new ThemeService();
        var loc = new LocalizationService();
        loc.Initialize(settings.Current);
        var manager = new WidgetManager(
            settings,
            theme,
            loc,
            environmentFactory: () => throw new InvalidOperationException("unused"),
            refreshSamplingDemand: _ => { },
            windowFactory: (_, _, _, _, _) => throw new InvalidOperationException("unused"));

        manager.Dispose();
        manager.Dispose();

        AssertNoSubscriberTarget(settings, "SettingsChanged", manager);
        AssertNoSubscriberTarget(theme, "ThemeChanged", manager);

        settings.Update(state => state.Font = "Segoe UI");
        theme.SetTheme(AppThemeColor.Green);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disposed_manager_does_not_create_display_service_when_read_or_pinned(bool setPinned)
    {
        var settings = TestSettings.Create();
        var theme = new ThemeService();
        var loc = new LocalizationService();
        loc.Initialize(settings.Current);
        int displayCreationAttempts = 0;

        var manager = new WidgetManager(
            settings,
            theme,
            loc,
            environmentFactory: () => throw new InvalidOperationException("WebView is not needed"),
            refreshSamplingDemand: _ => { },
            windowFactory: (_, _, _, _, _) => throw new InvalidOperationException("Window is not needed"),
            displayFactory: () =>
            {
                displayCreationAttempts++;
                throw new InvalidOperationException("DisplayService must not initialize after disposal");
            });

        manager.Dispose();
        WidgetStateSnapshot snapshot = setPinned
            ? manager.SetPinned("clock", pinned: true)
            : manager.GetSnapshot();

        Assert.Equal(0, displayCreationAttempts);
        Assert.NotEmpty(snapshot.Monitors);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("item")]
    [InlineData("show")]
    public void Disposed_manager_rejects_state_changes_and_window_creation(string action)
    {
        var settings = TestSettings.Create();
        var theme = new ThemeService();
        var loc = new LocalizationService();
        loc.Initialize(settings.Current);
        settings.Update(state =>
        {
            state.Widgets.Enabled = action == "show";
            foreach (WidgetItem item in state.Widgets.Items)
                item.Enabled = action == "show" && item.Type == "clock";
        });

        int settingsChanges = 0;
        int stateChanges = 0;
        int displayFactoryCalls = 0;
        int windowFactoryCalls = 0;
        int samplingRefreshCalls = 0;
        var manager = new WidgetManager(
            settings,
            theme,
            loc,
            environmentFactory: () => throw new InvalidOperationException("No WebView may be created"),
            refreshSamplingDemand: _ => samplingRefreshCalls++,
            windowFactory: (_, _, _, _, _) =>
            {
                windowFactoryCalls++;
                throw new InvalidOperationException("A disposed manager tried to create a window");
            },
            displayFactory: () =>
            {
                displayFactoryCalls++;
                throw new InvalidOperationException("A disposed manager tried to create a display service");
            });

        manager.Dispose();
        bool originalMasterEnabled = settings.Current.Widgets.Enabled;
        bool originalClockEnabled = settings.Current.Widgets.GetOrAdd("clock").Enabled;
        settings.SettingsChanged += _ => settingsChanges++;
        manager.StateChanged += _ => stateChanges++;

        // A disposed manager is terminal, even when a stale RPC/UI callback
        // invokes a mutator or a delayed ShowEnabled request.
        Exception? failure = Record.Exception(() =>
        {
            switch (action)
            {
                case "master":
                    manager.SetMasterEnabled(true);
                    break;
                case "item":
                    manager.SetEnabled("clock", true);
                    break;
                case "show":
                    manager.ShowEnabled();
                    break;
                default:
                    throw new InvalidOperationException("Unexpected test action");
            }
        });

        Assert.Null(failure);
        Assert.Equal(originalMasterEnabled, settings.Current.Widgets.Enabled);
        Assert.Equal(originalClockEnabled, settings.Current.Widgets.GetOrAdd("clock").Enabled);
        Assert.Equal(0, settingsChanges);
        Assert.Equal(0, stateChanges);
        Assert.Equal(0, displayFactoryCalls);
        Assert.Equal(0, windowFactoryCalls);
        Assert.Equal(0, samplingRefreshCalls);
    }

    private static void AssertNoSubscriberTarget(object publisher, string eventFieldName, WidgetManager manager)
    {
        FieldInfo? field = publisher.GetType().GetField(eventFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var callbacks = (field!.GetValue(publisher) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
        Assert.DoesNotContain(callbacks, callback => ReferenceEquals(callback.Target, manager));
    }
}
