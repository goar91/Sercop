using backend;
using backend.Endpoints;

namespace backend.tests;

public class EndpointValidationTests
{
    [Fact]
    public void ValidateSavedViewRequest_RechazaJsonInvalidoYViewTypeNoSoportado()
    {
        var request = new SavedViewUpsertRequest("dashboard", "Vista de prueba", "{not-json}", false);

        var errors = EndpointValidation.ValidateSavedViewRequest(request);

        Assert.Equal("El unico tipo de vista soportado actualmente es commercial.", errors["viewType"][0]);
        Assert.Equal("Los filtros serializados deben ser un objeto JSON valido.", errors["filtersJson"][0]);
    }

    [Fact]
    public void ValidateSavedViewRequest_AceptaObjetoJsonValido()
    {
        var request = new SavedViewUpsertRequest("commercial", "Vista vigente", """{"search":"laboratorio","todayOnly":true}""", true);

        var errors = EndpointValidation.ValidateSavedViewRequest(request);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateActivityRequest_RechazaBodyVacioYMetadataInvalida()
    {
        var request = new OpportunityActivityCreateRequest("note", " ", "{bad}");

        var errors = EndpointValidation.ValidateActivityRequest(request);

        Assert.Equal("Debes registrar el contenido de la actividad.", errors["body"][0]);
        Assert.Equal("Los metadatos deben ser un objeto JSON valido.", errors["metadataJson"][0]);
    }

    [Fact]
    public void ValidateInvitationUpdateRequest_RechazaUrlNoHttp()
    {
        var request = new OpportunityInvitationUpdateRequest(true, "manual_crm", "ftp://evidencia.local", "Soporte");

        var errors = EndpointValidation.ValidateInvitationUpdateRequest(request);

        Assert.Equal("La URL debe ser absoluta y usar http o https.", errors["invitationEvidenceUrl"][0]);
    }

    [Fact]
    public void ValidateBulkInvitationImportRequest_RechazaUrlInvalida()
    {
        var request = new BulkInvitationImportRequest("NIC-001", "manual_crm", "nota-local", "detalle");

        var errors = EndpointValidation.ValidateBulkInvitationImportRequest(request);

        Assert.Equal("La URL debe ser absoluta y usar http o https.", errors["invitationEvidenceUrl"][0]);
    }

    [Fact]
    public void ValidateOpportunityFilters_AceptaKeywordOpcional()
    {
        var errors = EndpointValidation.ValidateOpportunityFilters("nco_infimas", "  pH  ");

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateOpportunityFilters_RechazaKeywordDemasiadoLarga()
    {
        var keyword = new string('x', 161);

        var errors = EndpointValidation.ValidateOpportunityFilters("all", keyword);

        Assert.Equal("La palabra clave no puede superar 160 caracteres.", errors["keyword"][0]);
    }
}
