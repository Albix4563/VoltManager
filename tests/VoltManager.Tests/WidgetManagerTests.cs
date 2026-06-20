using System.Windows;
using VoltManager.Services;

namespace VoltManager.Tests;

public class WidgetManagerTests
{
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
