namespace backend.tests;

public class ChemistryOpportunityPolicyTests
{
    [Fact]
    public void ShouldDisplay_AceptaProcesoQuimicoDeLaboratorio()
    {
        var result = ChemistryOpportunityPolicy.ShouldDisplay(
            "Reactivos y solventes para laboratorio de control de calidad de agua",
            "Empresa publica de agua potable",
            "Necesidad de contratacion",
            CreateSnapshot());

        Assert.True(result);
    }

    [Fact]
    public void ShouldDisplay_RechazaProcesosMedicosAunqueContenganReactivos()
    {
        var result = ChemistryOpportunityPolicy.ShouldDisplay(
            "Reactivos para laboratorio clinico y diagnostico hospitalario",
            "Hospital general",
            "Recepcion de proformas",
            CreateSnapshot());

        Assert.False(result);
    }

    [Fact]
    public void ShouldDisplay_RespetaExcludeKeywordsAdministrables()
    {
        var result = ChemistryOpportunityPolicy.ShouldDisplay(
            "Solvente quimico para laboratorio de aguas",
            "Empresa publica",
            "Proceso de contratacion",
            CreateSnapshot(excludeKeywords: ["solvente quimico"]));

        Assert.False(result);
    }

    [Fact]
    public void ResolveWinningAreas_DeduplicaYUsaLasFamiliasConfiguradas()
    {
        var areas = ChemistryOpportunityPolicy.ResolveWinningAreas(
            ["Reactivo", "reactivo", "Solvente", "desconocido"],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["reactivo"] = "Reactivos",
                ["solvente"] = "Solventes",
            });

        Assert.Equal(["Reactivos", "Solventes"], areas);
    }

    private static KeywordRuleSnapshot CreateSnapshot(
        string[]? includeKeywords = null,
        string[]? excludeKeywords = null,
        Dictionary<string, string>? familyByKeyword = null)
        => new(
            includeKeywords ?? [],
            excludeKeywords ?? [],
            familyByKeyword ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
