using VoltManager.Models;

namespace VoltManager.Services;

internal sealed class BrightnessService
{
    private readonly Func<DisplayBrightnessState> _reader;
    private readonly Action<byte> _writer;

    public BrightnessService(
        Func<DisplayBrightnessState>? reader = null,
        Action<byte>? writer = null)
    {
        _reader = reader ?? ReadFromWmi;
        _writer = writer ?? WriteToWmi;
    }

    public DisplayBrightnessState GetBrightness()
    {
        try
        {
            return _reader();
        }
        catch (Exception ex)
        {
            Logger.Warn("Display brightness query failed: " + ex.Message);
            return Unsupported();
        }
    }

    public DisplayBrightnessState SetBrightness(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        DisplayBrightnessState current = GetBrightness();
        if (!current.Supported)
            return current;

        try
        {
            _writer((byte)percent);
            return GetBrightness();
        }
        catch (Exception ex)
        {
            Logger.Warn("Display brightness update failed: " + ex.Message);
            return Unsupported();
        }
    }

    private static DisplayBrightnessState ReadFromWmi()
    {
        IReadOnlyList<DisplayBrightnessState> values = WmiQuery.Read(
            @"root\WMI",
            "SELECT Active, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE",
            obj =>
            {
                int? percent = null;
                object? value = obj["CurrentBrightness"];
                if (value != null)
                    percent = Math.Clamp(Convert.ToInt32(value), 0, 100);
                return new DisplayBrightnessState { Supported = true, Percent = percent };
            });
        return values.FirstOrDefault() ?? Unsupported();
    }

    private static void WriteToWmi(byte percent)
    {
        // Failures must reach SetBrightness so it reports Unsupported instead of a silent no-op.
        _ = WmiQuery.ReadOrThrow(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE",
            obj =>
            {
                obj.InvokeMethod("WmiSetBrightness", new object[] { 1u, percent });
                return true;
            });
    }

    private static DisplayBrightnessState Unsupported()
        => new() { Supported = false, Percent = null };
}
