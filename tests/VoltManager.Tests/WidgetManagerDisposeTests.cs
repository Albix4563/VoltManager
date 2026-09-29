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

    private static void AssertNoSubscriberTarget(object publisher, string eventFieldName, WidgetManager manager)
    {
        FieldInfo? field = publisher.GetType().GetField(eventFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var callbacks = (field!.GetValue(publisher) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
        Assert.DoesNotContain(callbacks, callback => ReferenceEquals(callback.Target, manager));
    }
}
