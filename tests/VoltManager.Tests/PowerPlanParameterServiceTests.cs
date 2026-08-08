using VoltManager.Services;

namespace VoltManager.Tests;

public class PowerPlanParameterServiceTests
{
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
}
