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

    [Fact]
    public void CalculateCascadePosition_StartsTopRightAndOffsets()
    {
        var workArea = new Rect(0, 0, 1200, 800);
        var size = new Size(300, 200);

        var first = WidgetManager.CalculateCascadePosition(workArea, 0, size);
        var second = WidgetManager.CalculateCascadePosition(workArea, 1, size);

        Assert.Equal(876, first.X);
        Assert.Equal(24, first.Y);
        Assert.Equal(852, second.X);
        Assert.Equal(48, second.Y);
    }

    [Fact]
    public void CalculateCascadePosition_ClampsInsideWorkArea()
    {
        var workArea = new Rect(100, 50, 360, 260);
        var size = new Size(340, 230);

        var point = WidgetManager.CalculateCascadePosition(workArea, 20, size);

        Assert.True(point.X >= workArea.Left + 8);
        Assert.True(point.X + size.Width <= workArea.Right - 8);
        Assert.True(point.Y >= workArea.Top);
        Assert.True(point.Y + size.Height <= workArea.Bottom);
    }
}
