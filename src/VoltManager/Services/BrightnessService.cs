using System.Management;
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
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT Active, CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                int? percent = null;
                object? value = obj["CurrentBrightness"];
                if (value != null)
                    percent = Math.Clamp(Convert.ToInt32(value), 0, 100);

                return new DisplayBrightnessState { Supported = true, Percent = percent };
            }
        }

        return Unsupported();
    }

    private static void WriteToWmi(byte percent)
    {
        using var searcher = new ManagementObjectSearcher(
            @"root\WMI",
            "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
                obj.InvokeMethod("WmiSetBrightness", new object[] { 1u, percent });
        }
    }

    private static DisplayBrightnessState Unsupported()
        => new() { Supported = false, Percent = null };
}
