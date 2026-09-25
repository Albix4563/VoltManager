using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class BrightnessServiceTests
{
    [Fact]
    public void GetBrightness_returns_unsupported_when_reader_throws()
    {
        var service = new BrightnessService(
            reader: () => throw new InvalidOperationException("WMI unavailable"),
            writer: _ => { });

        DisplayBrightnessState state = service.GetBrightness();

        Assert.False(state.Supported);
        Assert.Null(state.Percent);
    }

    [Fact]
    public void SetBrightness_does_not_write_when_display_is_unsupported()
    {
        int writes = 0;
        var service = new BrightnessService(
            reader: () => new DisplayBrightnessState { Supported = false, Percent = null },
            writer: _ => writes++);

        DisplayBrightnessState state = service.SetBrightness(55);

        Assert.False(state.Supported);
        Assert.Null(state.Percent);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void SetBrightness_writes_then_rereads_current_brightness()
    {
        int reads = 0;
        byte? written = null;
        var service = new BrightnessService(
            reader: () =>
            {
                reads++;
                return new DisplayBrightnessState { Supported = true, Percent = reads == 1 ? 40 : 73 };
            },
            writer: value => written = value);

        DisplayBrightnessState state = service.SetBrightness(73);

        Assert.Equal((byte)73, written);
        Assert.Equal(2, reads);
        Assert.True(state.Supported);
        Assert.Equal(73, state.Percent);
    }
}
