using System.Runtime.InteropServices;
using VoltManager.Services;

namespace VoltManager.Tests;

public sealed class WindowsPowerCapabilitiesTests
{
    [Fact]
    public void NativeCapabilitiesLayoutMatchesWindowsStructureSize()
    {
        Assert.Equal(76, WindowsPowerCapabilities.NativeCapabilitiesSize);
        Assert.Equal(16, Marshal.OffsetOf<WindowsPowerCapabilities.SystemPowerCapabilities>(
            nameof(WindowsPowerCapabilities.SystemPowerCapabilities.ProcessorMaxThrottle)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<WindowsPowerCapabilities.SystemPowerCapabilities>(
            nameof(WindowsPowerCapabilities.SystemPowerCapabilities.AoAc)).ToInt32());
        Assert.Equal(22, Marshal.OffsetOf<WindowsPowerCapabilities.SystemPowerCapabilities>(
            nameof(WindowsPowerCapabilities.SystemPowerCapabilities.HiberFileType)).ToInt32());
        Assert.Equal(30, Marshal.OffsetOf<WindowsPowerCapabilities.SystemPowerCapabilities>(
            nameof(WindowsPowerCapabilities.SystemPowerCapabilities.SystemBatteriesPresent)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<WindowsPowerCapabilities.SystemPowerCapabilities>(
            nameof(WindowsPowerCapabilities.SystemPowerCapabilities.BatteryScale0)).ToInt32());
        Assert.Equal(56, Marshal.OffsetOf<WindowsPowerCapabilities.SystemPowerCapabilities>(
            nameof(WindowsPowerCapabilities.SystemPowerCapabilities.AcOnLineWake)).ToInt32());
    }

    [Fact]
    public void ModernStandbyCountsAsSleepWithoutLegacySStates()
    {
        WindowsPowerCapabilityState result = WindowsPowerCapabilities.Evaluate(
            systemS1: false,
            systemS2: false,
            systemS3: false,
            systemS4: false,
            hiberFilePresent: false,
            aoAc: true,
            hiberFileType: null);

        Assert.True(result.SleepAvailable);
        Assert.False(result.HibernateAvailable);
    }

    [Fact]
    public void LegacySleepStateCountsAsSleep()
    {
        WindowsPowerCapabilityState result = WindowsPowerCapabilities.Evaluate(
            systemS1: false,
            systemS2: false,
            systemS3: true,
            systemS4: false,
            hiberFilePresent: false,
            aoAc: false,
            hiberFileType: null);

        Assert.True(result.SleepAvailable);
    }

    [Theory]
    [InlineData(true, true, null, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, true, 3, false)]
    [InlineData(true, true, 2, true)]
    [InlineData(true, true, 1, false)]
    [InlineData(false, true, 2, false)]
    [InlineData(true, false, 2, false)]
    public void HibernateRequiresS4PresentHiberfileAndConfirmedFullType(
        bool systemS4,
        bool hiberFilePresent,
        int? hiberFileType,
        bool expected)
    {
        WindowsPowerCapabilityState result = WindowsPowerCapabilities.Evaluate(
            systemS1: false,
            systemS2: false,
            systemS3: false,
            systemS4: systemS4,
            hiberFilePresent: hiberFilePresent,
            aoAc: false,
            hiberFileType: hiberFileType);

        Assert.Equal(expected, result.HibernateAvailable);
    }
}
