using VoltManager.Services;

namespace VoltManager.Fans;

public sealed record FanBackendWriteResult
{
    public bool Success { get; init; }
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public double? EffectiveControlPercent { get; init; }
    public string? Mode { get; init; }
}

public interface IFanBackend
{
    string Name { get; }
    bool CanHandle(FanDevice fan);
    FanBackendWriteResult SetSoftware(FanDevice fan, double percent);
    FanBackendWriteResult RestoreDefault(FanDevice fan);
}

/// <summary>
/// Fan backend backed exclusively by explicit LibreHardwareMonitor IControl
/// channels discovered by HardwareAccessCoordinator. It never writes to raw EC,
/// Super I/O registers, WMI fan methods, or guessed vendor interfaces.
/// </summary>
public sealed class LibreHardwareMonitorFanBackend : IFanBackend
{
    private readonly IHardwareAccess _hardware;

    public string Name => "libre-hardware-monitor";

    public LibreHardwareMonitorFanBackend(IHardwareAccess hardware)
    {
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
    }

    public bool CanHandle(FanDevice fan) =>
        _hardware.ControlWritesAllowed &&
        fan.Capabilities.Backend.Equals(Name, StringComparison.OrdinalIgnoreCase) &&
        fan.Capabilities.ControlWritable &&
        !string.IsNullOrWhiteSpace(fan.ControlIdentifier);

    public FanBackendWriteResult SetSoftware(FanDevice fan, double percent)
    {
        if (!CanHandle(fan))
            return Fail("backend_unavailable", "LibreHardwareMonitor does not expose a writable control for this fan.");

        HardwareFanControlResult result = _hardware.SetFanSoftware(fan.ControlIdentifier!, percent);
        return FromHardware(result);
    }

    public FanBackendWriteResult RestoreDefault(FanDevice fan)
    {
        if (string.IsNullOrWhiteSpace(fan.ControlIdentifier))
            return Fail("restore_unavailable", "The fan has no control channel that can be restored.");

        HardwareFanControlResult result = _hardware.RestoreFanDefault(fan.ControlIdentifier);
        return FromHardware(result);
    }

    private static FanBackendWriteResult FromHardware(HardwareFanControlResult result) => new()
    {
        Success = result.Ok,
        Code = result.Code,
        Message = result.Message,
        EffectiveControlPercent = result.Control?.SoftwareValue,
        Mode = result.Control?.Mode,
    };

    private static FanBackendWriteResult Fail(string code, string message) => new()
    {
        Success = false,
        Code = code,
        Message = message,
    };
}
