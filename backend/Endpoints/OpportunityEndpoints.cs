using System.Text;
using backend.Auth;

namespace backend.Endpoints;

internal static class OpportunityEndpoints
{
    public static IEndpointRouteBuilder MapOpportunityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Commercial);

        group.MapGet("/opportunities", async (
            HttpContext context,
            string? search,
            string? estado,
            long? zoneId,
            long? assignedUserId,
            bool? invitedOnly,
            bool? todayOnly,
            int? page,
            int? pageSize,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var result = await repository.SearchOpportunitiesAsync(
                search,
                estado,
                zoneId,
                assignedUserId,
                invitedOnly ?? false,
                todayOnly ?? false,
                page ?? 1,
                pageSize ?? 25,
                EndpointContext.GetActor(context),
                cancellationToken);

            return Results.Ok(result);
        });

        group.MapGet("/opportunities/export", async (
            HttpContext context,
            string? format,
            string? search,
            string? estado,
            long? zoneId,
            long? assignedUserId,
            bool? invitedOnly,
            bool? todayOnly,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var result = await repository.SearchOpportunitiesAsync(
                search,
                estado,
                zoneId,
                assignedUserId,
                invitedOnly ?? false,
                todayOnly ?? false,
                1,
                100,
                EndpointContext.GetActor(context),
                cancellationToken);
            var normalizedFormat = (format ?? "csv").Trim().ToLowerInvariant();
            return normalizedFormat == "excel"
                ? Results.File(BuildExcelPayload(result.Items), "application/vnd.ms-excel", $"procesos-sercop-{DateTime.UtcNow:yyyyMMddHHmmss}.xls")
                : Results.File(Encoding.UTF8.GetBytes(BuildCsvPayload(result.Items)), "text/csv; charset=utf-8", $"procesos-sercop-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
        });

        group.MapGet("/opportunities/visibility", async (
            HttpContext context,
            string code,
            bool? todayOnly,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var visibility = await repository.GetOpportunityVisibilityAsync(code, todayOnly ?? false, EndpointContext.GetActor(context), cancellationToken);
            return Results.Ok(visibility);
        });

        group.MapPost("/opportunities/import-by-code", async (
            HttpContext context,
            ImportOpportunityByCodeRequest request,
            IConfiguration configuration,
            CrmRepository repository,
            SercopPublicClient sercopPublicClient,
            CancellationToken cancellationToken) =>
        {
            var code = request.Code?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["Debes indicar un codigo de proceso."]
                });
            }

            var fallbackYear = int.TryParse(configuration["OCDS_YEAR"], out var configuredYear)
                ? configuredYear
                : DateTime.UtcNow.Year;
            var detail = await repository.ImportOpportunityByCodeAsync(code, sercopPublicClient, fallbackYear, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound(new
                {
                    code,
                    message = "No se encontro el proceso en las fuentes publicas del SERCOP."
                });
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(
                actor?.Id,
                actor?.LoginName,
                "opportunity_import_by_code",
                "opportunity",
                detail.Id.ToString(),
                EndpointContext.GetClientIp(context),
                EndpointContext.GetUserAgent(context),
                new { code, detail.Source, detail.ProcessCode },
                cancellationToken);

            return Results.Ok(detail);
        }).RequireAuthorization(CrmPolicies.CommercialManagers);

        group.MapGet("/opportunities/{id:long}", async (HttpContext context, long id, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var detail = await repository.GetOpportunityAsync(id, EndpointContext.GetActor(context), cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapGet("/opportunities/{id:long}/activities", async (
            HttpContext context,
            long id,
            int? page,
            int? pageSize,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            var detail = await repository.GetOpportunityAsync(id, actor, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var activities = await repository.GetOpportunityActivitiesAsync(id, page ?? 1, pageSize ?? 20, cancellationToken);
            return Results.Ok(activities);
        });

        group.MapPost("/opportunities/{id:long}/activities", async (
            HttpContext context,
            long id,
            OpportunityActivityCreateRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateActivityRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = EndpointContext.GetActor(context);
            var detail = await repository.GetOpportunityAsync(id, actor, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var activity = await repository.AddOpportunityActivityAsync(id, actor?.Id, actor?.LoginName, request, EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), cancellationToken);
            return activity is null ? Results.NotFound() : Results.Created($"/api/opportunities/{id}/activities/{activity.Id}", activity);
        });

        group.MapPut("/opportunities/{id:long}/reminder", async (
            HttpContext context,
            long id,
            OpportunityReminderUpsertRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateReminderRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = EndpointContext.GetActor(context);
            var detail = await repository.GetOpportunityAsync(id, actor, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            var reminder = await repository.UpsertReminderAsync(id, actor?.Id, actor?.LoginName, request, EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), cancellationToken);
            return Results.Ok(reminder);
        });

        group.MapPut("/opportunities/{id:long}/assignment", async (
            HttpContext context,
            long id,
            OpportunityAssignmentRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var currentDetail = await repository.GetOpportunityAsync(id, actor, cancellationToken);
            if (currentDetail is null)
            {
                return Results.NotFound();
            }

            OpportunityAssignmentRequest effectiveRequest;
            if (CrmRoleRules.IsSeller(actor))
            {
                effectiveRequest = request with
                {
                    AssignedUserId = currentDetail.AssignedUserId,
                    ZoneId = currentDetail.ZoneId
                };
            }
            else if (CrmRoleRules.CanManageCommercialAssignments(actor))
            {
                effectiveRequest = request;
            }
            else
            {
                return Results.Forbid();
            }

            var errors = await EndpointValidation.ValidateAssignmentRequestAsync(effectiveRequest, repository, cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var detail = await repository.UpdateAssignmentAsync(id, effectiveRequest, actor.Id, actor, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            await repository.WriteAuditLogAsync(
                actor?.Id,
                actor?.LoginName,
                "assignment_update",
                "opportunity",
                id.ToString(),
                EndpointContext.GetClientIp(context),
                EndpointContext.GetUserAgent(context),
                new { effectiveRequest.AssignedUserId, effectiveRequest.ZoneId, effectiveRequest.Estado, effectiveRequest.Priority },
                cancellationToken);

            return Results.Ok(detail);
        });

        group.MapPut("/opportunities/{id:long}/invitation", async (
            HttpContext context,
            long id,
            OpportunityInvitationUpdateRequest request,
            IConfiguration configuration,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateInvitationUpdateRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = EndpointContext.GetActor(context);
            if (!CrmRoleRules.CanManageCommercialAssignments(actor))
            {
                return Results.Forbid();
            }

            var currentDetail = await repository.GetOpportunityAsync(id, actor, cancellationToken);
            if (currentDetail is null)
            {
                return Results.NotFound();
            }

            var invitedCompanyName = configuration["INVITED_COMPANY_NAME"] ?? "HDM";
            var detail = await repository.UpdateInvitationAsync(id, request, invitedCompanyName, actor?.Id, actor, cancellationToken);
            if (detail is null)
            {
                return Results.NotFound();
            }

            await repository.WriteAuditLogAsync(
                actor?.Id,
                actor?.LoginName,
                "invitation_update",
                "opportunity",
                id.ToString(),
                EndpointContext.GetClientIp(context),
                EndpointContext.GetUserAgent(context),
                new { request.IsInvitedMatch, request.InvitationSource, request.InvitationEvidenceUrl },
                cancellationToken);

            return Results.Ok(detail);
        });

        group.MapPost("/invitations/import", async (
            HttpContext context,
            BulkInvitationImportRequest request,
            IConfiguration configuration,
            CrmRepository repository,
            SercopPublicClient sercopPublicClient,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.CodesText))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["codesText"] = ["Debes ingresar al menos un codigo de proceso."]
                });
            }

            var invitedCompanyName = configuration["INVITED_COMPANY_NAME"] ?? "HDM";
            var fallbackYear = int.TryParse(configuration["OCDS_YEAR"], out var configuredYear) ? configuredYear : DateTime.UtcNow.Year;
            var result = await repository.BulkImportInvitationsAsync(request, sercopPublicClient, invitedCompanyName, fallbackYear, cancellationToken);

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(
                actor?.Id,
                actor?.LoginName,
                "invitation_import",
                "opportunity",
                null,
                EndpointContext.GetClientIp(context),
                EndpointContext.GetUserAgent(context),
                new { result.RequestedCount, result.ConfirmedCount },
                cancellationToken);

            return Results.Ok(result);
        }).RequireAuthorization(CrmPolicies.CommercialManagers);

        group.MapPost("/invitations/sync", async (
            IConfiguration configuration,
            CrmRepository repository,
            SercopInvitationPublicClient invitationClient,
            CancellationToken cancellationToken) =>
        {
            var invitedCompanyName = configuration["INVITED_COMPANY_NAME"] ?? "HDM";
            var invitedCompanyRuc = configuration["INVITED_COMPANY_RUC"];
            var result = await repository.SyncInvitationsFromPublicReportsAsync(invitationClient, invitedCompanyName, invitedCompanyRuc, cancellationToken);
            return Results.Ok(result);
        }).RequireAuthorization(CrmPolicies.Operations);

        group.MapGet("/commercial/alerts", async (
            HttpContext context,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var alerts = await repository.GetCommercialAlertsAsync(actor, cancellationToken);
            return Results.Ok(alerts);
        });

        group.MapGet("/commercial/views", async (
            HttpContext context,
            string? viewType,
            int? page,
            int? pageSize,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context)!;
            var views = await repository.GetSavedViewsAsync(actor.Id, viewType, page ?? 1, pageSize ?? 20, cancellationToken);
            return Results.Ok(views);
        });

        group.MapPost("/commercial/views", async (
            HttpContext context,
            SavedViewUpsertRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateSavedViewRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = EndpointContext.GetActor(context)!;
            var view = await repository.UpsertSavedViewAsync(null, actor.Id, request, actor.Id, actor.LoginName, EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), cancellationToken);
            return view is null ? Results.Problem(statusCode: 500, title: "No se pudo guardar la vista") : Results.Created($"/api/commercial/views/{view.Id}", view);
        });

        group.MapPut("/commercial/views/{id:long}", async (
            HttpContext context,
            long id,
            SavedViewUpsertRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateSavedViewRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = EndpointContext.GetActor(context)!;
            var view = await repository.UpsertSavedViewAsync(id, actor.Id, request, actor.Id, actor.LoginName, EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), cancellationToken);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });

        group.MapDelete("/commercial/views/{id:long}", async (
            HttpContext context,
            long id,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context)!;
            var deleted = await repository.DeleteSavedViewAsync(id, actor.Id, actor.Id, actor.LoginName, EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }

    private static string BuildCsvPayload(IEnumerable<OpportunityListItemDto> rows)
    {
        static string Escape(string? value)
            => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

        var builder = new StringBuilder();
        builder.AppendLine("Codigo,Titulo,Entidad,Tipo,FechaPublicacion,FechaLimite,Estado,Asignado,Zona,InvitadoHDM,Sla");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',',
                Escape(row.ProcessCode),
                Escape(row.Titulo),
                Escape(row.Entidad),
                Escape(row.Tipo),
                Escape(row.FechaPublicacion?.ToString("yyyy-MM-dd HH:mm")),
                Escape(row.FechaLimite?.ToString("yyyy-MM-dd HH:mm")),
                Escape(row.Estado),
                Escape(row.AssignedUserName),
                Escape(row.ZoneName),
                Escape(row.IsInvitedMatch ? "Si" : "No"),
                Escape(row.SlaStatus)));
        }

        return builder.ToString();
    }

    private static byte[] BuildExcelPayload(IEnumerable<OpportunityListItemDto> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<html><head><meta charset=\"utf-8\"></head><body><table border=\"1\">");
        builder.AppendLine("<tr><th>Codigo</th><th>Titulo</th><th>Entidad</th><th>Tipo</th><th>Fecha publicacion</th><th>Fecha limite</th><th>Estado</th><th>Asignado</th><th>Zona</th><th>Invitado HDM</th><th>SLA</th></tr>");

        foreach (var row in rows)
        {
            builder.Append("<tr>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.ProcessCode)}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.Titulo)}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.Entidad)}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.Tipo)}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.FechaPublicacion?.ToString("yyyy-MM-dd HH:mm"))}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.FechaLimite?.ToString("yyyy-MM-dd HH:mm"))}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.Estado)}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.AssignedUserName)}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.ZoneName)}</td>");
            builder.Append($"<td>{(row.IsInvitedMatch ? "Si" : "No")}</td>");
            builder.Append($"<td>{System.Net.WebUtility.HtmlEncode(row.SlaStatus)}</td>");
            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</table></body></html>");
        return Encoding.UTF8.GetBytes(builder.ToString());
    }
}
