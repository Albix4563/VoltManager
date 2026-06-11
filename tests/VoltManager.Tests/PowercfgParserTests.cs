using VoltManager.Models;
using VoltManager.Services;

namespace VoltManager.Tests;

public class PowercfgParserTests
{
    // Real-world Italian Windows output shape.
    private const string ItalianList = @"Combinazioni risparmio energia esistenti (* Attiva)
-----------------------------------
GUID combinazione risparmio energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Bilanciato) *
GUID combinazione risparmio energia: 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Prestazioni elevate)
GUID combinazione risparmio energia: a1841308-3541-4fab-bc81-f71556f20b4a  (Risparmio di energia)
";

    [Fact]
    public void ParsesItalianOutput_AllThreePlans()
    {
        var plans = PowerPlanService.ParseListOutput(ItalianList);
        Assert.Equal(3, plans.Count);
        Assert.Contains(plans, p => p.PlanId == PlanId.Balanced && p.IsActive);
        Assert.Contains(plans, p => p.PlanId == PlanId.Performance && !p.IsActive);
        Assert.Contains(plans, p => p.PlanId == PlanId.PowerSaver);
    }

    [Fact]
    public void ParsesLocalizedNames()
    {
        var plans = PowerPlanService.ParseListOutput(ItalianList);
        Assert.Equal("Bilanciato", plans.First(p => p.PlanId == PlanId.Balanced).Name);
    }

    [Fact]
    public void UnknownGuid_PlanIdNull()
    {
        var plans = PowerPlanService.ParseListOutput(
            "GUID combinazione risparmio energia: 11111111-2222-3333-4444-555555555555  (Custom)");
        Assert.Single(plans);
        Assert.Null(plans[0].PlanId);
    }

    [Fact]
    public void GuidMap_ResolvesDuplicatedScheme()
    {
        var map = new Dictionary<string, string>
        {
            ["Performance"] = "11111111-2222-3333-4444-555555555555",
        };
        var resolved = PowerPlanService.ResolvePlanId("11111111-2222-3333-4444-555555555555", map);
        Assert.Equal(PlanId.Performance, resolved);
    }

    [Fact]
    public void EmptyOutput_NoPlans()
    {
        Assert.Empty(PowerPlanService.ParseListOutput(""));
    }

    [Fact]
    public void GetActiveSchemeLine_Parsed()
    {
        var plans = PowerPlanService.ParseListOutput(
            "GUID combinazione risparmio energia: 381b4222-f694-41f0-9685-ff5bb260df2e  (Bilanciato)");
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e", plans[0].Guid);
    }
}
