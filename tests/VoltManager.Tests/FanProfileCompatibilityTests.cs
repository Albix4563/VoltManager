using VoltManager.Fans;
using Xunit;

namespace VoltManager.Tests;

public class FanProfileCompatibilityTests
{
    [Fact]
    public void Analyze_matches_unique_role_when_hardware_ids_differ()
    {
        var topology = new FanTopology
        {
            Revision = "rev-1",
            SensorsAvailable = true,
            Devices = new List<FanDevice>
            {
                new()
                {
                    Id = "local-cpu",
                    HardwareId = "board|cpu fan|0",
                    HardwareName = "Board",
                    SensorName = "CPU Fan",
                    DisplayName = "CPU Fan",
                    Role = FanRole.CpuFan,
                    RoleConfidence = FanDetectionConfidence.High,
                    ControlState = FanControlState.MonitorOnly,
                    Capabilities = FanCapabilities.MonitorOnly,
                }
            }
        };
        var profile = new FanProfile
        {
            Id = "profile-1",
            Name = "Desktop",
            Fans = new List<FanProfileFan>
            {
                new()
                {
                    ProfileFanId = "source-cpu",
                    DisplayName = "CPU Fan",
                    MatchHints = new FanMatchHints { Role = FanRole.CpuFan, HeaderName = "CPU_FAN" }
                }
            }
        };

        var report = new FanProfileCompatibilityAnalyzer().Analyze(profile, topology);

        var item = Assert.Single(report.Items);
        Assert.Equal(FanProfileMatchStatus.Matched, item.Status);
        Assert.Equal("local-cpu", item.MatchedFanId);
        Assert.True(report.CanStore);
        Assert.False(report.CanApplyControl);
    }

    [Fact]
    public void Analyze_does_not_guess_when_multiple_role_candidates_exist()
    {
        var topology = new FanTopology
        {
            Revision = "rev-2",
            SensorsAvailable = true,
            Devices = new List<FanDevice>
            {
                Device("fan-a", FanRole.CaseFan, "SYS_FAN1"),
                Device("fan-b", FanRole.CaseFan, "SYS_FAN2"),
            }
        };
        var profile = new FanProfile
        {
            Id = "profile-2",
            Name = "Case",
            Fans = new List<FanProfileFan>
            {
                new()
                {
                    ProfileFanId = "rear",
                    DisplayName = "Rear Exhaust",
                    MatchHints = new FanMatchHints { Role = FanRole.CaseFan }
                }
            }
        };

        var item = Assert.Single(new FanProfileCompatibilityAnalyzer().Analyze(profile, topology).Items);

        Assert.Equal(FanProfileMatchStatus.NeedsMapping, item.Status);
        Assert.Null(item.MatchedFanId);
        Assert.Equal(2, item.CandidateFanIds.Count);
    }

    [Fact]
    public void Validate_rejects_unsafe_or_malformed_curve_data()
    {
        var profile = new FanProfile
        {
            Id = "profile-3",
            Name = "Unsafe",
            Fans = new List<FanProfileFan>
            {
                new()
                {
                    ProfileFanId = "cpu",
                    DisplayName = "CPU Fan",
                    MatchHints = new FanMatchHints { Role = FanRole.CpuFan },
                    Configuration = new FanConfiguration
                    {
                        Mode = FanMode.Curve,
                        Curve = new List<FanCurvePoint>
                        {
                            new() { Temperature = 60, ControlPercent = 80 },
                            new() { Temperature = 80, ControlPercent = 30 },
                        }
                    }
                }
            }
        };

        var result = FanProfileValidator.Validate(profile);

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, error => error.Contains("monotonic", StringComparison.OrdinalIgnoreCase));
    }

    private static FanDevice Device(string id, FanRole role, string header) => new()
    {
        Id = id,
        HardwareId = "board|" + header,
        HardwareName = "Board",
        SensorName = header,
        HeaderName = header,
        DisplayName = header,
        Role = role,
        RoleConfidence = FanDetectionConfidence.High,
        ControlState = FanControlState.MonitorOnly,
        Capabilities = FanCapabilities.MonitorOnly,
    };
}
