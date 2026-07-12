using System.Windows;
using VoltManager.Services;

namespace VoltManager.Tests;

public class WidgetManagerTests
{
    [Theory]
    [InlineData("clock", "mini", 180, 96)]
    [InlineData("clock", "medium", 260, 150)]
    [InlineData("clock", "large", 340, 200)]
    [InlineData("calendar", "mini", 190, 120)]
    [InlineData("calendar", "medium", 320, 330)]
    [InlineData("calendar", "large", 420, 430)]
    [InlineData("usage", "mini", 220, 118)]
    [InlineData("usage", "medium", 300, 220)]
    [InlineData("usage", "large", 390, 285)]
    [InlineData("temps", "mini", 210, 110)]
    [InlineData("temps", "medium", 280, 180)]
    [InlineData("temps", "large", 360, 235)]
    [InlineData("power", "mini", 220, 118)]
    [InlineData("power", "medium", 300, 230)]
    [InlineData("power", "large", 390, 300)]
    [InlineData("plans", "mini", 280, 108)]
    [InlineData("plans", "medium", 340, 150)]
    [InlineData("plans", "large", 420, 190)]
    public void GetWidgetSize_ReturnsPresetDimensions(string type, string preset, double width, double height)
    {
        var size = WidgetManager.GetWidgetSize(type, preset);

        Assert.Equal(width, size.Width);
        Assert.Equal(height, size.Height);
    }

    [Fact]
    public void GetWidgetSize_DefaultsToMedium()
    {
        Assert.Equal(WidgetManager.GetWidgetSize("usage", "medium"), WidgetManager.GetWidgetSize("usage"));
        Assert.Equal(WidgetManager.GetWidgetSize("usage", "medium"), WidgetManager.GetWidgetSize("usage", "huge"));
    }

    [Theory]
    [InlineData("topLeft", 24, 24)]
    [InlineData("topCenter", 450, 24)]
    [InlineData("topRight", 876, 24)]
    [InlineData("middleLeft", 24, 300)]
    [InlineData("center", 450, 300)]
    [InlineData("middleRight", 876, 300)]
    [InlineData("bottomLeft", 24, 576)]
    [InlineData("bottomCenter", 450, 576)]
    [InlineData("bottomRight", 876, 576)]
    public void Calculate_PositionsFirstWidgetAtAnchor(string anchor, double x, double y)
    {
        var display = TestDisplay(new PixelRect(0, 0, 1200, 800));
        var result = WidgetLayout.Calculate(
            [new LayoutRequest("clock", display.Id, anchor, 0, 0, new Size(300, 200))],
            new DisplaySnapshot([display], true));

        Assert.Equal(new PixelRect(x, y, 300, 200), result[0].FinalBounds);
    }

    [Fact]
    public void Calculate_StacksSameAnchorInCanonicalOrder()
    {
        var display = TestDisplay(new PixelRect(0, 0, 1200, 800));
        var size = new Size(300, 200);
        var result = WidgetLayout.Calculate(
        [
            new LayoutRequest("clock", display.Id, "topLeft", 0, 0, size),
            new LayoutRequest("calendar", display.Id, "topLeft", 0, 0, size),
            new LayoutRequest("usage", display.Id, "topLeft", 0, 0, size),
        ],
        new DisplaySnapshot([display], true));

        Assert.Equal(new[] { "clock", "calendar", "usage" }, result.Select(r => r.Type).ToArray());
        Assert.Equal(24, result[0].FinalBounds.Y);
        Assert.Equal(236, result[1].FinalBounds.Y); // 24 + 200 + 12
        Assert.Equal(448, result[2].FinalBounds.Y); // 236 + 200 + 12
        Assert.True(NoOverlap(result[0].FinalBounds, result[1].FinalBounds));
        Assert.True(NoOverlap(result[1].FinalBounds, result[2].FinalBounds));
        Assert.True(NoOverlap(result[0].FinalBounds, result[2].FinalBounds));
    }

    [Fact]
    public void Calculate_OverflowsByManhattanThenReadingOrder()
    {
        // Height only fits one 200px widget with margins: 24 + 200 + 24 = 248.
        var display = TestDisplay(new PixelRect(0, 0, 1200, 260));
        var size = new Size(300, 200);
        var result = WidgetLayout.Calculate(
        [
            new LayoutRequest("clock", display.Id, "topLeft", 0, 0, size),
            new LayoutRequest("calendar", display.Id, "topLeft", 0, 0, size),
        ],
        new DisplaySnapshot([display], true));

        Assert.Equal("topLeft", result[0].EffectiveAnchor);
        // next manhattan neighbor of topLeft is topCenter (or middleLeft). Reading-order
        // after manhattan: topCenter (index 1) before middleLeft (index 3).
        Assert.Equal("topCenter", result[1].EffectiveAnchor);
        Assert.True(NoOverlap(result[0].FinalBounds, result[1].FinalBounds));
    }

    [Fact]
    public void Calculate_SeparatesCollisionSetsByMonitor()
    {
        var left = TestDisplay(new PixelRect(-1920, 0, 1920, 1080), "left", isPrimary: false, number: 2);
        var primary = TestDisplay(new PixelRect(0, 0, 1920, 1080), "primary", isPrimary: true, number: 1);
        var size = new Size(300, 200);

        var result = WidgetLayout.Calculate(
        [
            new LayoutRequest("clock", left.Id, "topLeft", 0, 0, size),
            new LayoutRequest("calendar", primary.Id, "topLeft", 0, 0, size),
        ],
        new DisplaySnapshot([left, primary], true));

        Assert.Equal(left.Id, result[0].EffectiveDisplay.Id);
        Assert.Equal(primary.Id, result[1].EffectiveDisplay.Id);
        Assert.Equal(result[0].FinalBounds.Y, result[1].FinalBounds.Y);
    }

    [Fact]
    public void Calculate_PreservesValidOffsetAndClampsInvalidOffset()
    {
        var display = TestDisplay(new PixelRect(0, 0, 1200, 800));
        var size = new Size(300, 200);

        var valid = WidgetLayout.Calculate(
            [new LayoutRequest("clock", display.Id, "topLeft", 10, 15, size)],
            new DisplaySnapshot([display], true))[0];
        Assert.Equal(10, valid.AppliedOffsetX);
        Assert.Equal(15, valid.AppliedOffsetY);
        Assert.Equal(34, valid.FinalBounds.X);
        Assert.Equal(39, valid.FinalBounds.Y);

        var invalid = WidgetLayout.Calculate(
            [new LayoutRequest("clock", display.Id, "topLeft", 5000, 5000, size)],
            new DisplaySnapshot([display], true))[0];
        Assert.True(invalid.FinalBounds.Right <= display.WorkArea.Right + 0.5);
        Assert.True(invalid.FinalBounds.Bottom <= display.WorkArea.Bottom + 0.5);
        Assert.True(invalid.AppliedOffsetX < 5000);
        Assert.True(invalid.AppliedOffsetY < 5000);
    }

    [Fact]
    public void Calculate_UsesPrimaryTemporarilyWithoutChangingDesiredDisplay()
    {
        var primary = TestDisplay(new PixelRect(0, 0, 1200, 800), "primary", isPrimary: true);
        var result = WidgetLayout.Calculate(
            [new LayoutRequest("clock", "missing-monitor", "topRight", 0, 0, new Size(300, 200))],
            new DisplaySnapshot([primary], true))[0];

        Assert.True(result.UsesFallbackDisplay);
        Assert.Equal("missing-monitor", result.DesiredDisplay.Id);
        Assert.Equal(primary.Id, result.EffectiveDisplay.Id);
    }

    [Fact]
    public void Calculate_ScalesDimensionsAndOffsetsAt150Percent()
    {
        var display = TestDisplay(new PixelRect(0, 0, 1800, 1200), "scaled", isPrimary: true, scaleX: 1.5, scaleY: 1.5);
        var result = WidgetLayout.Calculate(
            [new LayoutRequest("clock", display.Id, "topLeft", 10, 20, new Size(300, 200))],
            new DisplaySnapshot([display], true))[0];

        // margin 24*1.5=36, size 450x300, offset 15x30 px
        Assert.Equal(450, result.FinalBounds.Width);
        Assert.Equal(300, result.FinalBounds.Height);
        Assert.Equal(36 + 15, result.FinalBounds.X);
        Assert.Equal(36 + 30, result.FinalBounds.Y);
        Assert.Equal(10, result.AppliedOffsetX);
        Assert.Equal(20, result.AppliedOffsetY);
    }

    [Fact]
    public void MigrateLegacy_SelectsMaximumIntersectionAndPreservesBounds()
    {
        var left = TestDisplay(new PixelRect(-1920, 0, 1920, 1080), "left", isPrimary: false, number: 2);
        var primary = TestDisplay(new PixelRect(0, 0, 1920, 1080), "primary", isPrimary: true, number: 1);
        var legacy = new PixelRect(-400, 100, 300, 200);

        var migrated = WidgetLayout.MigrateLegacy(legacy, new DisplaySnapshot([left, primary], true));
        var replay = WidgetLayout.Calculate(
            [new LayoutRequest("clock", migrated.Display.Id, migrated.Anchor, migrated.OffsetX, migrated.OffsetY, new Size(300, 200))],
            new DisplaySnapshot([left, primary], true))[0];

        Assert.Equal("left", migrated.Display.Id);
        Assert.InRange(Math.Abs(replay.FinalBounds.X - legacy.X), 0, 1);
        Assert.InRange(Math.Abs(replay.FinalBounds.Y - legacy.Y), 0, 1);
    }

    private static DisplayInfo TestDisplay(
        PixelRect workArea,
        string id = "display-1",
        bool isPrimary = true,
        int number = 1,
        double scaleX = 1,
        double scaleY = 1)
        => new(id, number, "Test", workArea, scaleX, scaleY, isPrimary);

    private static bool NoOverlap(PixelRect a, PixelRect b)
        => !a.Intersects(b, WidgetLayout.GapDip, WidgetLayout.GapDip);
}
