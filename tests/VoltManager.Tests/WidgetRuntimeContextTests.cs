using System.Reflection;
using VoltManager.Localization;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WidgetRuntimeContextTests
{
    [Fact]
    public void Widget_components_do_not_store_App()
    {
        Assert.DoesNotContain(typeof(WidgetManager).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => field.FieldType == typeof(App));

        Assert.DoesNotContain(typeof(WidgetWindow).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
            field => field.FieldType == typeof(App));
    }

    [Fact]
    public void Widget_manager_construction_does_not_create_webview_environment()
    {
        var settings = TestSettings.Create();
        var theme = new ThemeService();
        var loc = new LocalizationService();
        loc.Initialize(settings.Current);
        int environmentCalls = 0;

        using var manager = new WidgetManager(
            settings,
            theme,
            loc,
            environmentFactory: () =>
            {
                environmentCalls++;
                throw new InvalidOperationException("environment must stay lazy");
            },
            refreshSamplingDemand: _ => { },
            windowFactory: (_, _, _, _, _) =>
                throw new InvalidOperationException("no window expected during construction"));

        Assert.Equal(0, environmentCalls);
    }
}
