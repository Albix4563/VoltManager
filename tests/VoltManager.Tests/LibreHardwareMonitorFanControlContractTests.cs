using LibreHardwareMonitor.Hardware;
using Xunit;

namespace VoltManager.Tests;

public class LibreHardwareMonitorFanControlContractTests
{
    [Fact]
    public void Installed_lhm_contract_exposes_explicit_sensor_control_only_when_present()
    {
        var sensorProperties = typeof(ISensor).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var controlMethods = typeof(IControl).GetMethods().Select(method => method.Name).ToHashSet(StringComparer.Ordinal);
        var controlProperties = typeof(IControl).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Control", sensorProperties);
        Assert.Contains("Identifier", sensorProperties);
        Assert.Contains("SetDefault", controlMethods);
        Assert.Contains("SetSoftware", controlMethods);
        Assert.Contains("MinSoftwareValue", controlProperties);
        Assert.Contains("MaxSoftwareValue", controlProperties);
        Assert.Contains("SoftwareValue", controlProperties);
        Assert.Contains("ControlMode", controlProperties);
        Assert.Contains("Software", Enum.GetNames(typeof(ControlMode)));
        Assert.Contains("Default", Enum.GetNames(typeof(ControlMode)));
    }
}
