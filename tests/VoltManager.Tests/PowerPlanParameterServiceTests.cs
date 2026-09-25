using VoltManager.Services;

namespace VoltManager.Tests;

public class PowerPlanParameterServiceTests
{
    [Fact]
    public void GetPlanTimeouts_reads_display_brightness_percentages()
    {
        const string guid = PowerPlanService.BalancedGuid;
        string RunPowercfg(string args)
        {
            if (args == "/list")
                return $"Power Scheme GUID: {guid}  (Balanced) *";
            if (args.Contains("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", StringComparison.Ordinal))
                return Indexes(300, 120);
            if (args.Contains("aded5e82-b909-4619-9949-f5d71dac0bcb", StringComparison.Ordinal))
                return Indexes(80, 35);
            if (args.Contains("29f6c1db-86da-48c5-9fdb-f2b67b1f44da", StringComparison.Ordinal))
                return Indexes(600, 300);
            return "";
        }

        var power = new PowerPlanService(TestSettings.Create(), () => Guid.Parse(guid), RunPowercfg);
        var service = new PowerPlanParameterService(power);

        var result = service.GetPlanTimeouts(guid);

        Assert.Equal(80, result.DisplayBrightnessAc);
        Assert.Equal(35, result.DisplayBrightnessDc);
        Assert.Null(result.Error);
    }

    [Fact]
    public void GetPlanTimeouts_leaves_display_brightness_null_when_setting_is_absent()
    {
        const string guid = PowerPlanService.BalancedGuid;
        string RunPowercfg(string args)
        {
            if (args == "/list")
                return $"Power Scheme GUID: {guid}  (Balanced) *";
            if (args.Contains("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", StringComparison.Ordinal))
                return Indexes(300, 120);
            if (args.Contains("29f6c1db-86da-48c5-9fdb-f2b67b1f44da", StringComparison.Ordinal))
                return Indexes(600, 300);
            return "";
        }

        var power = new PowerPlanService(TestSettings.Create(), () => Guid.Parse(guid), RunPowercfg);
        var service = new PowerPlanParameterService(power);

        var result = service.GetPlanTimeouts(guid);

        Assert.Null(result.DisplayBrightnessAc);
        Assert.Null(result.DisplayBrightnessDc);
        Assert.Null(result.Error);
    }

    [Fact]
    public void SetPlanParameter_displayBrightness_uses_video_brightness_setting_and_clamps_range()
    {
        const string guid = PowerPlanService.BalancedGuid;
        var commands = new List<string>();
        string RunPowercfg(string args)
        {
            commands.Add(args);
            if (args == "/list")
                return $"Power Scheme GUID: {guid}  (Balanced) *";
            if (args.StartsWith("/qh ", StringComparison.Ordinal))
            {
                return """
    Minimum Possible Setting: 0x00000000
    Maximum Possible Setting: 0x00000064
    Current AC Power Setting Index: 0x00000064
    Current DC Power Setting Index: 0x00000000
""";
            }
            return "";
        }

        var power = new PowerPlanService(TestSettings.Create(), () => Guid.Parse(guid), RunPowercfg);
        var service = new PowerPlanParameterService(power);

        bool ok = service.SetPlanParameter(guid, "displayBrightness", 150, -5);

        Assert.True(ok);
        Assert.Contains($"/setacvalueindex {guid} 7516b95f-f776-4464-8c53-06167f40cc99 aded5e82-b909-4619-9949-f5d71dac0bcb 100", commands);
        Assert.Contains($"/setdcvalueindex {guid} 7516b95f-f776-4464-8c53-06167f40cc99 aded5e82-b909-4619-9949-f5d71dac0bcb 0", commands);
    }

    [Fact]
    public void TryParseCurrentIndexes_parses_localized_italian_powercfg_output()
    {
        const string output = """
GUID combinazione risparmio energia: 906662eb-8c87-46e1-9ff1-9548cb110d77  (Risparmio di energia)
  GUID sottogruppo: 7516b95f-f776-4464-8c53-06167f40cc99  (Schermo)
    GUID impostazioni risparmio energia: 3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e  (Disattiva schermo dopo)
      Minima impostazione possibile: 0x00000000
      Massima impostazione possibile: 0xffffffff
      Incremento impostazioni possibile: 0x00000001
    Indice impostazione alimentazione CA corrente: 0x0000012c
    Indice impostazione alimentazione CC corrente: 0x00000078
""";

        bool ok = PowerPlanParameterService.TryParseCurrentIndexes(output, out int ac, out int dc);

        Assert.True(ok);
        Assert.Equal(300, ac);
        Assert.Equal(120, dc);
    }

    [Fact]
    public void TryParseCurrentIndexes_uses_final_hex_values_for_hidden_settings()
    {
        const string output = """
Power Scheme GUID: 00000000-0000-0000-0000-000000000000
  Power Setting GUID: 36687f9e-e3a5-4dbf-b1dc-15eb381c6863
    Minimum Possible Setting: 0x00000000
    Maximum Possible Setting: 0x00000064
    Possible Settings increment: 0x00000001
    Current AC Power Setting Index: 0x0000003c
    Current DC Power Setting Index: 0x00000050
""";

        bool ok = PowerPlanParameterService.TryParseCurrentIndexes(output, out int ac, out int dc);

        Assert.True(ok);
        Assert.Equal(60, ac);
        Assert.Equal(80, dc);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nessun indice disponibile")]
    [InlineData("solo un valore 0x00000001")]
    public void TryParseCurrentIndexes_rejects_incomplete_output(string output)
    {
        Assert.False(PowerPlanParameterService.TryParseCurrentIndexes(output, out _, out _));
    }

    private static string Indexes(int ac, int dc)
        => $"Current AC Power Setting Index: 0x{ac:x8}\nCurrent DC Power Setting Index: 0x{dc:x8}";
}
