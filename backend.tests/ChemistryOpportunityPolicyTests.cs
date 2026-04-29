namespace backend.tests;

public class ChemistryOpportunityPolicyTests
{
    [Fact]
    public void ShouldDisplay_AceptaProcesoCuandoCoincideConInclude()
    {
        var result = ChemistryOpportunityPolicy.ShouldDisplay(
            "Reactivos y solventes para laboratorio de control de calidad de agua",
            "Empresa publica de agua potable",
            "Necesidad de contratacion",
            CreateSnapshot(includeKeywords: ["reactivos", "solventes"]));

        Assert.True(result);
    }

    [Fact]
    public void ShouldDisplay_RechazaProcesoSinIncludeAunqueTengaContextoHistoricamenteValido()
    {
        var result = ChemistryOpportunityPolicy.ShouldDisplay(
            "Reactivos para laboratorio clinico y diagnostico hospitalario",
            "Hospital general",
            "Recepcion de proformas",
            CreateSnapshot());

        Assert.False(result);
    }

    [Fact]
    public void ShouldDisplay_AplicaKeywordsSinImportarTildesONormalizacion()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Reactivos para química analítica",
            "Universidad pública",
            "Necesidad de contratación",
            null,
            CreateSnapshot(includeKeywords: ["quimica analitica"]));

        Assert.True(evaluation.IsVisible);
        Assert.Contains("quimica analitica", evaluation.IncludeHits);
    }

    [Fact]
    public void ShouldDisplay_RechazaCuandoCoincideConExcludeAunqueTambienTengaInclude()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Adquirir campana de extraccion para laboratorio docente",
            "Universidad publica",
            "Necesidad de contratacion",
            null,
            CreateSnapshot(includeKeywords: ["campana de extraccion"], excludeKeywords: ["laboratorio docente"]));

        Assert.False(evaluation.IsVisible);
        Assert.Contains("laboratorio docente", evaluation.ExcludeHits);
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
    public void ShouldDisplay_RechazaProcesoMedicoAunqueTengaInclude()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Reactivos para laboratorio clinico hospitalario",
            "Hospital general",
            "Recepcion de proformas",
            null,
            CreateSnapshot(includeKeywords: ["reactivos"]));

        Assert.False(evaluation.IsVisible);
    }

    [Fact]
    public void ShouldDisplay_AceptaEquipoMicrobiologicoSiElExcludeEsGenerico()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "ADQUISICION DE EQUIPOS MICROBIOLÓGICOS PARA EL LABORATORIO DE ALIMENTOS",
            "Gobierno autonomo descentralizado",
            "Infimas cuantias",
            null,
            CreateSnapshot(
                includeKeywords: ["laboratorio", "microbiologicos"],
                excludeKeywords: ["adquisicion de equipos"],
                familyByKeyword: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["laboratorio"] = "laboratorio",
                    ["microbiologicos"] = "laboratorio",
                    ["adquisicion de equipos"] = "ruido"
                }));

        Assert.True(evaluation.IsVisible);
        Assert.DoesNotContain("adquisicion de equipos", evaluation.ExcludeHits, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldDisplay_NoExcluyePorSenalMedicaSoloEnDireccionRaw()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Adquisición de material de campo para el procesamiento de muestras biológicas",
            "Direccion de investigacion",
            "Infimas cuantias",
            "Direccion entrega: Centro de Genetica Medica",
            CreateSnapshot(
                includeKeywords: ["muestras biologicas"],
                excludeKeywords: ["medica"],
                familyByKeyword: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["muestras biologicas"] = "laboratorio",
                    ["medica"] = "medico"
                }));

        Assert.True(evaluation.IsVisible);
    }

    [Fact]
    public void ShouldDisplay_NoRechazaCombustibleSiHayHidrocarburos()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Adquisicion de pastas detectoras para combustibles e hidrocarburos",
            "Empresa publica",
            "Necesidad de contratacion",
            null,
            CreateSnapshot(includeKeywords: ["hidrocarburos"]));

        Assert.True(evaluation.IsVisible);
        Assert.DoesNotContain(evaluation.Reasons, reason => reason.Contains("señales no quimicas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShouldDisplay_RechazaProcesoFarmaceuticoAunqueTengaInclude()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Reactivos para control de calidad 10 mg/ml",
            "Empresa publica",
            "Necesidad de contratacion",
            null,
            CreateSnapshot(includeKeywords: ["reactivos"]));

        Assert.False(evaluation.IsVisible);
    }

    [Fact]
    public void ShouldDisplay_RechazaIncludeNoQuimicoSegunFamilia()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Recepcion de proformas para adquisicion de servicios generales",
            "Entidad publica",
            "Recepcion de proformas",
            null,
            CreateSnapshot(
                includeKeywords: ["recepcion de proformas"],
                familyByKeyword: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["recepcion de proformas"] = "nco"
                }));

        Assert.False(evaluation.IsVisible);
    }

    [Fact]
    public void EvaluateScored_AsignaScoreVisibleMinimoCuandoHayInclude()
    {
        var evaluation = ChemistryOpportunityPolicy.EvaluateScored(
            "Reactivos para laboratorio",
            "Empresa publica",
            "Proceso de contratacion",
            null,
            CreateSnapshot(includeKeywords: ["reactivos"]));

        Assert.True(evaluation.IsVisible);
        Assert.True(evaluation.MatchScore >= 60m);
        Assert.Equal("revisar", evaluation.Recommendation);
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
            familyByKeyword ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            (includeKeywords ?? []).ToDictionary(keyword => keyword, _ => 1m, StringComparer.OrdinalIgnoreCase),
            (excludeKeywords ?? []).ToDictionary(keyword => keyword, _ => 1m, StringComparer.OrdinalIgnoreCase));
}
