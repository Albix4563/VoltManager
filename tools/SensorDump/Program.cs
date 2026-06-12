using System.Management;
using System.Security.Principal;
using System.Text;

// Diagnostic v2: probe every WMI thermal source elevated.
var sb = new StringBuilder();
bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
sb.AppendLine($"Elevated: {elevated}");

void Probe(string scope, string query, Func<ManagementObject, string> fmt, string label)
{
    sb.AppendLine($"--- {label} ---");
    try
    {
        using var searcher = new ManagementObjectSearcher(scope, query);
        int n = 0;
        foreach (ManagementObject mo in searcher.Get())
        {
            sb.AppendLine("  " + fmt(mo));
            n++;
        }
        if (n == 0) sb.AppendLine("  (no instances)");
    }
    catch (Exception ex)
    {
        sb.AppendLine("  FAILED: " + ex.Message);
    }
}

Probe(@"root\WMI", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
    mo => $"{mo["InstanceName"]}: {Convert.ToDouble(mo["CurrentTemperature"]) / 10.0 - 273.15:F1} C",
    "MSAcpi_ThermalZoneTemperature");

Probe(@"root\CIMV2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation",
    mo => $"{mo["Name"]}: T={mo["Temperature"]} HP={mo["HighPrecisionTemperature"]}",
    "ThermalZoneInformation perf counters");

Probe(@"root\CIMV2", "SELECT * FROM Win32_TemperatureProbe",
    mo => $"{mo["Name"]}: {mo["CurrentReading"]}",
    "Win32_TemperatureProbe");

File.WriteAllText(@"C:\power_efficency\sensordump.txt", sb.ToString());
Console.WriteLine("written");
