using System.Text.Json;
using System.Windows;
using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WidgetCustomizationTests
{
    [Fact]
    public void WidgetSettings_adds_apps_after_launcher_and_uses_shared_size_presets()
    {
        int launcher = Array.IndexOf(WidgetSettings.Types, "launcher");
        Assert.Equal("apps", WidgetSettings.Types[launcher + 1]);
        Assert.Equal(WidgetSettings.Sizes, WidgetSettings.LauncherSizes);
        Assert.Equal("medium", WidgetSettings.NormalizeSize("launcher", "bar"));
        Assert.Equal("medium", WidgetSettings.NormalizeSize("apps", "column"));
        Assert.Equal("medium", WidgetSettings.NormalizeSize("clock", "bar"));
    }

    [Fact]
    public void Normalize_migrates_legacy_launcher_sizes_and_clamps_only_length_axis()
    {
        var settings = new WidgetSettings
        {
            Items =
            [
                new WidgetItem { Type = "launcher", Size = "bar", Width = 500, Height = 90 },
                new WidgetItem { Type = "apps", Size = "column", Width = 90, Height = 500 },
                new WidgetItem { Type = "clock", Orientation = "vertical", Width = 900, Height = 700 },
            ],
        };

        settings.Normalize();

        var launcher = settings.Items.Single(x => x.Type == "launcher");
        Assert.Equal("medium", launcher.Size);
        Assert.Equal("horizontal", launcher.Orientation);
        Assert.Equal(500, launcher.Width);
        Assert.Null(launcher.Height);
        var apps = settings.Items.Single(x => x.Type == "apps");
        Assert.Equal("medium", apps.Size);
        Assert.Equal("vertical", apps.Orientation);
        Assert.Null(apps.Width);
        Assert.Equal(500, apps.Height);
        var clock = settings.Items.Single(x => x.Type == "clock");
        Assert.Equal("horizontal", clock.Orientation);
        Assert.Null(clock.Width);
        Assert.Null(clock.Height);
    }

    [Fact]
    public void Normalize_validates_orientation_and_launcher_length_bounds()
    {
        var settings = new WidgetSettings
        {
            Items =
            [
                new WidgetItem { Type = "launcher", Size = "mini", Orientation = "diagonal", Width = 20 },
                new WidgetItem { Type = "apps", Size = "large", Orientation = "vertical", Height = 5000 },
            ],
        };
        settings.Normalize();

        var launcher = settings.Items.Single(x => x.Type == "launcher");
        Assert.Equal("horizontal", launcher.Orientation);
        Assert.Equal(WidgetSettings.LauncherMinLength("mini"), launcher.Width);
        Assert.Null(launcher.Height);

        var apps = settings.Items.Single(x => x.Type == "apps");
        Assert.Equal("vertical", apps.Orientation);
        Assert.Null(apps.Width);
        Assert.Equal(WidgetSettings.LauncherMaxLength("large"), apps.Height);
    }

    [Fact]
    public void Normalize_infers_vertical_for_legacy_tall_custom_launcher_without_orientation()
    {
        var item = JsonSerializer.Deserialize<WidgetItem>(
            """{"type":"launcher","size":"mini","width":100,"height":1000}""")!;
        var settings = new WidgetSettings { Items = [item] };

        settings.Normalize();

        var launcher = settings.Items.Single(x => x.Type == "launcher");
        Assert.Equal("vertical", launcher.Orientation);
        Assert.Null(launcher.Width);
        Assert.Equal(502, launcher.Height);
    }

    [Fact]
    public void Normalize_infers_horizontal_for_legacy_wide_custom_launcher_without_orientation()
    {
        var item = JsonSerializer.Deserialize<WidgetItem>(
            """{"type":"launcher","size":"medium","width":700,"height":300}""")!;
        var settings = new WidgetSettings { Items = [item] };

        settings.Normalize();

        var launcher = settings.Items.Single(x => x.Type == "launcher");
        Assert.Equal("horizontal", launcher.Orientation);
        Assert.Equal(582, launcher.Width);
        Assert.Null(launcher.Height);
    }

    [Fact]
    public void Normalize_keeps_explicit_launcher_orientation_over_legacy_aspect_ratio()
    {
        var item = JsonSerializer.Deserialize<WidgetItem>(
            """{"type":"launcher","size":"large","orientation":"horizontal","width":650,"height":1000}""")!;
        var settings = new WidgetSettings { Items = [item] };

        settings.Normalize();

        var launcher = settings.Items.Single(x => x.Type == "launcher");
        Assert.Equal("horizontal", launcher.Orientation);
        Assert.Equal(650, launcher.Width);
        Assert.Null(launcher.Height);
    }

    [Fact]
    public void GetWidgetSize_keeps_launcher_thickness_fixed_and_length_bounded()
    {
        Assert.Equal(62, WidgetSettings.LauncherChromeLength);
        Assert.Equal(194, WidgetSettings.LauncherMinLength("mini"));
        Assert.Equal(374, WidgetSettings.LauncherDefaultLength("medium"));
        Assert.Equal(742, WidgetSettings.LauncherMaxLength("large"));
        Assert.Equal(new Size(374, 66), WidgetManager.GetWidgetSize("launcher", "medium"));

        var horizontal = new WidgetItem { Type = "launcher", Size = "mini", Orientation = "horizontal", Width = 5000 };
        Assert.Equal(new Size(502, 58), WidgetManager.GetWidgetSize(horizontal));

        var vertical = new WidgetItem { Type = "apps", Size = "large", Orientation = "vertical", Height = 5000 };
        Assert.Equal(new Size(82, 742), WidgetManager.GetWidgetSize(vertical));
    }

    [Theory]
    [InlineData("mini", 44, 58, 194, 326, 502)]
    [InlineData("medium", 52, 66, 218, 374, 582)]
    [InlineData("large", 68, 82, 266, 470, 742)]
    public void Launcher_bar_geometry_fits_six_slots_exactly_in_both_orientations(
        string size, double slot, double thickness, double minLength, double defaultLength, double maxLength)
    {
        Assert.Equal(thickness, WidgetSettings.LauncherThickness(size));
        Assert.Equal(minLength, WidgetSettings.LauncherMinLength(size));
        Assert.Equal(defaultLength, WidgetSettings.LauncherDefaultLength(size));
        Assert.Equal(maxLength, WidgetSettings.LauncherMaxLength(size));

        var horizontal = WidgetManager.GetWidgetSize(new WidgetItem
        {
            Type = "launcher",
            Size = size,
            Orientation = "horizontal",
        });
        var vertical = WidgetManager.GetWidgetSize(new WidgetItem
        {
            Type = "launcher",
            Size = size,
            Orientation = "vertical",
        });

        Assert.Equal(defaultLength, horizontal.Width);
        Assert.Equal(thickness, horizontal.Height);
        Assert.Equal(thickness, vertical.Width);
        Assert.Equal(defaultLength, vertical.Height);

        const double border = 2;
        const double bodyPadding = 12;
        const double leadingCap = 34;
        const double resizeCap = 14;
        Assert.Equal(slot, horizontal.Height - bodyPadding - border);
        Assert.Equal(slot * 6, horizontal.Width - leadingCap - bodyPadding - resizeCap - border);
        Assert.Equal(slot, vertical.Width - bodyPadding - border);
        Assert.Equal(slot * 6, vertical.Height - leadingCap - bodyPadding - resizeCap - border);
    }

    [Fact]
    public void WidgetLayout_clamps_oversized_widgets_to_monitor_work_area_margin()
    {
        var display = new DisplayInfo(
            "display-1", 1, "Display 1",
            new PixelRect(0, 0, 500, 300),
            1, 1, true);
        var placements = WidgetLayout.Calculate(
            [new LayoutRequest("clock", "display-1", "topLeft", 0, 0, new Size(2000, 1200))],
            new DisplaySnapshot([display], true));

        var bounds = Assert.Single(placements).FinalBounds;
        Assert.Equal(500 - WidgetLayout.MarginDip * 2, bounds.Width);
        Assert.Equal(300 - WidgetLayout.MarginDip * 2, bounds.Height);
        Assert.True(bounds.X >= display.WorkArea.X);
        Assert.True(bounds.Y >= display.WorkArea.Y);
        Assert.True(bounds.Right <= display.WorkArea.Right);
        Assert.True(bounds.Bottom <= display.WorkArea.Bottom);
    }

    [Fact]
    public void SetSize_clears_custom_dimensions()
    {
        var settings = TestSettings.Create();
        settings.Update(state =>
        {
            var item = state.Widgets.GetOrAdd("launcher");
            item.Width = 700;
            item.Orientation = "horizontal";
        });
        var theme = new ThemeService();
        var loc = new VoltManager.Localization.LocalizationService();
        loc.Initialize(settings.Current);
        using var manager = new WidgetManager(
            settings, theme, loc,
            environmentFactory: () => throw new InvalidOperationException("unused"),
            refreshSamplingDemand: _ => { },
            windowFactory: (_, _, _, _, _) => throw new InvalidOperationException("unused"));

        manager.SetSize("launcher", "large");

        var item = settings.Current.Widgets.GetOrAdd("launcher");
        Assert.Equal("large", item.Size);
        Assert.Equal("horizontal", item.Orientation);
        Assert.Null(item.Width);
        Assert.Null(item.Height);
    }

    [Fact]
    public void SetOrientation_resets_custom_length()
    {
        var settings = TestSettings.Create();
        settings.Update(state =>
        {
            var item = state.Widgets.GetOrAdd("apps");
            item.Orientation = "horizontal";
            item.Width = 400;
        });
        var theme = new ThemeService();
        var loc = new VoltManager.Localization.LocalizationService();
        loc.Initialize(settings.Current);
        using var manager = new WidgetManager(
            settings, theme, loc,
            environmentFactory: () => throw new InvalidOperationException("unused"),
            refreshSamplingDemand: _ => { },
            windowFactory: (_, _, _, _, _) => throw new InvalidOperationException("unused"));

        manager.SetOrientation("apps", "vertical");

        var item = settings.Current.Widgets.GetOrAdd("apps");
        Assert.Equal("vertical", item.Orientation);
        Assert.Null(item.Width);
        Assert.Null(item.Height);
    }
}
