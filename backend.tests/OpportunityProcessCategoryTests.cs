namespace backend.tests;

public class OpportunityProcessCategoryTests
{
    [Theory]
    [InlineData("nco", "Ínfimas Cuantías", "NIC-1760005620001-2026-00090", OpportunityProcessCategory.Infimas)]
    [InlineData("nco", "Necesidades de Contratación", "NC-1760005620001-2026-00090", OpportunityProcessCategory.Nco)]
    [InlineData("nco", "Recepción de Proformas", "NC-1760005620001-2026-00090", OpportunityProcessCategory.Nco)]
    [InlineData("ocds", "Régimen Especial", "RE-001", OpportunityProcessCategory.Re)]
    [InlineData("ocds", "Subasta Inversa Electrónica", "SIE-001", OpportunityProcessCategory.Sie)]
    [InlineData("ocds", "Menor Cuantía", "MC-001", OpportunityProcessCategory.OtherPublic)]
    public void Resolve_ClasificaProcesosSegunFuenteYTipo(string source, string tipo, string code, string expected)
    {
        var category = OpportunityProcessCategory.Resolve(source, tipo, code);

        Assert.Equal(expected, category);
    }

    [Theory]
    [InlineData(null, OpportunityProcessCategory.All)]
    [InlineData("", OpportunityProcessCategory.All)]
    [InlineData("all", OpportunityProcessCategory.All)]
    [InlineData("infimas", OpportunityProcessCategory.Infimas)]
    [InlineData("nco", OpportunityProcessCategory.Nco)]
    [InlineData("sie", OpportunityProcessCategory.Sie)]
    [InlineData("re", OpportunityProcessCategory.Re)]
    [InlineData("other_public", OpportunityProcessCategory.OtherPublic)]
    [InlineData("nco_infimas", OpportunityProcessCategory.LegacyNcoInfimas)]
    [InlineData("regimen_especial", OpportunityProcessCategory.LegacyRegimenEspecial)]
    [InlineData("procesos_contratacion", OpportunityProcessCategory.LegacyProcesosContratacion)]
    public void NormalizeFilter_NormalizaValoresSoportados(string? rawValue, string expected)
    {
        Assert.Equal(expected, OpportunityProcessCategory.NormalizeFilter(rawValue));
    }

    [Fact]
    public void IsValidFilter_RechazaValoresDesconocidos()
    {
        Assert.False(OpportunityProcessCategory.IsValidFilter("desconocido"));
    }

    [Theory]
    [InlineData(OpportunityProcessCategory.Infimas, OpportunityProcessCategory.LegacyNcoInfimas, true)]
    [InlineData(OpportunityProcessCategory.Nco, OpportunityProcessCategory.LegacyNcoInfimas, true)]
    [InlineData(OpportunityProcessCategory.Sie, OpportunityProcessCategory.LegacyProcesosContratacion, true)]
    [InlineData(OpportunityProcessCategory.OtherPublic, OpportunityProcessCategory.LegacyProcesosContratacion, true)]
    [InlineData(OpportunityProcessCategory.Re, OpportunityProcessCategory.LegacyRegimenEspecial, true)]
    [InlineData(OpportunityProcessCategory.Nco, OpportunityProcessCategory.Sie, false)]
    public void MatchesFilter_SoportaAliasesLegados(string category, string filter, bool expected)
    {
        Assert.Equal(expected, OpportunityProcessCategory.MatchesFilter(category, filter));
    }
}
