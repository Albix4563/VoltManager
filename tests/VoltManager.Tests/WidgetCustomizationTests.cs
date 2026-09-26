using System.Windows;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WidgetCustomizationTests
{
    [Fact]
    public void WidgetSettings_adds_apps_after_launcher_and_limits_special_sizes_to_launcher_types()
    {
        int launcher = Array.IndexOf(WidgetSettings.Types, "launcher");
        Assert.Equal("apps", WidgetSettings.Types[launcher + 1]);

        Assert.Equal("bar", WidgetSettings.NormalizeSize("launcher", "bar"));
        Assert.Equal("column", WidgetSettings.NormalizeSize("apps", "column"));
        Assert.Equal("medium", WidgetSettings.NormalizeSize("clock", "bar"));
        Assert.Equal("medium", WidgetSettings.NormalizeSize("power", "column"));
    }

    [Fact]
    public void Normalize_clamps_custom_dimensions_only_for_launcher_and_apps()
    {
        var settings = new WidgetSettings
        {
            Items =
            [
                new WidgetItem { Type = "launcher", Width = 20, Height = 5000 },
                new WidgetItem { Type = "apps", Width = double.NaN, Height = double.PositiveInfinity },
                new WidgetItem { Type = "clock", Width = 900, Height = 700 },
            ],
        };

        settings.Normalize();

        var launcher = settings.Items.Single(x => x.Type == "launcher");
        Assert.Equal(56, launcher.Width);
        Assert.Equal(1080, launcher.Height);
        var apps = settings.Items.Single(x => x.Type == "apps");
        Assert.Null(apps.Width);
        Assert.Null(apps.Height);
        var clock = settings.Items.Single(x => x.Type == "clock");
        Assert.Null(clock.Width);
        Assert.Null(clock.Height);
    }

    [Fact]
    public void GetWidgetSize_uses_special_presets_and_custom_dimensions()
    {
        Assert.Equal(new Size(520, 64), WidgetManager.GetWidgetSize("launcher", "bar"));
        Assert.Equal(new Size(64, 520), WidgetManager.GetWidgetSize("apps", "column"));
        Assert.Equal(WidgetManager.GetWidgetSize("clock", "medium"), WidgetManager.GetWidgetSize("clock", "bar"));

        var item = new WidgetItem { Type = "apps", Size = "bar", Width = 700, Height = 80 };
        Assert.Equal(new Size(700, 80), WidgetManager.GetWidgetSize(item));
    }

    [Fact]
    public void SetSize_clears_custom_dimensions()
    {
        var settings = TestSettings.Create();
        settings.Update(state =>
        {
            var item = state.Widgets.GetOrAdd("launcher");
            item.Width = 700;
            item.Height = 90;
        });
        var theme = new ThemeService();
        var loc = new VoltManager.Localization.LocalizationService();
        loc.Initialize(settings.Current);
        using var manager = new WidgetManager(
            settings, theme, loc,
            environmentFactory: () => throw new InvalidOperationException("unused"),
            refreshSamplingDemand: _ => { },
            windowFactory: (_, _, _, _, _) => throw new InvalidOperationException("unused"));

        manager.SetSize("launcher", "bar");

        var item = settings.Current.Widgets.GetOrAdd("launcher");
        Assert.Equal("bar", item.Size);
        Assert.Null(item.Width);
        Assert.Null(item.Height);
    }
}
