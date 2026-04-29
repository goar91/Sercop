using backend.Auth;

namespace backend.Endpoints;

internal static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var commercialGroup = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Commercial);
        var adminGroup = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Operations);
        var keywordGroup = app.MapGroup("/api").RequireAuthorization(CrmPolicies.KeywordManagers);

        commercialGroup.MapGet("/zones", async (CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var zones = await repository.GetZonesAsync(cancellationToken);
            return Results.Ok(zones);
        });

        commercialGroup.MapGet("/sercop/status", async (
            SercopCredentialVault credentialVault,
            SercopAuthenticatedClient sercopAuthenticatedClient,
            CancellationToken cancellationToken) =>
        {
            var status = await credentialVault.GetStatusAsync(cancellationToken);
            var session = sercopAuthenticatedClient.GetSessionSnapshot();
            return Results.Ok(new SercopOperationalStatusDto(
                status.Configured,
                status.MaskedRuc,
                status.MaskedUserName,
                status.ValidationStatus,
                status.LastValidatedAt,
                session.PortalSessionStatus,
                session.LastPortalLoginAt));
        });

        commercialGroup.MapGet("/users", async (
            HttpContext context,
            int? page,
            int? pageSize,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var users = await repository.SearchUsersAsync(page ?? 1, pageSize ?? 25, cancellationToken);
            if (!CrmRoleRules.IsSeller(actor))
            {
                return Results.Ok(users);
            }

            var scopedItems = users.Items.Where(user => user.Id == actor.Id).ToArray();
            return Results.Ok(new PagedResultDto<UserDto>(scopedItems, scopedItems.Length, 1, Math.Max(1, scopedItems.Length)));
        });

        adminGroup.MapPost("/zones", async (HttpContext context, ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateZoneRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var zone = await repository.UpsertZoneAsync(null, request, cancellationToken);
            if (zone is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar la zona.");
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "zone_create", "zone", zone.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { zone.Name, zone.Code, zone.Active }, cancellationToken);
            return Results.Created($"/api/zones/{zone.Id}", zone);
        });

        adminGroup.MapPut("/zones/{id:long}", async (HttpContext context, long id, ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateZoneRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var zone = await repository.UpsertZoneAsync(id, request, cancellationToken);
            if (zone is null)
            {
                return Results.NotFound();
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "zone_update", "zone", zone.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { zone.Name, zone.Code, zone.Active }, cancellationToken);
            return Results.Ok(zone);
        });

        adminGroup.MapPost("/users", async (
            HttpContext context,
            UserUpsertRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = await EndpointValidation.ValidateUserRequestAsync(request, repository, true, cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var user = await repository.UpsertUserAsync(null, request, cancellationToken);
            if (user is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar el usuario.");
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "user_create", "user", user.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { user.LoginName, user.Role }, cancellationToken);
            return Results.Created($"/api/users/{user.Id}", user);
        });

        adminGroup.MapPut("/users/{id:long}", async (
            HttpContext context,
            long id,
            UserUpsertRequest request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = await EndpointValidation.ValidateUserRequestAsync(request, repository, false, cancellationToken);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var user = await repository.UpsertUserAsync(id, request, cancellationToken);
            if (user is null)
            {
                return Results.NotFound();
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "user_update", "user", user.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { user.LoginName, user.Role }, cancellationToken);
            return Results.Ok(user);
        });

        keywordGroup.MapGet("/keywords", async (
            string? ruleType,
            string? scope,
            int? page,
            int? pageSize,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateKeywordRuleFilters(ruleType, scope);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var keywords = await repository.SearchKeywordRulesAsync(ruleType, scope, page ?? 1, pageSize ?? 25, cancellationToken);
            return Results.Ok(keywords);
        });

        keywordGroup.MapGet("/keywords/refresh-status", async (
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var refreshRun = await repository.GetLatestKeywordRefreshRunAsync(cancellationToken);
            return Results.Ok(refreshRun);
        });

        keywordGroup.MapPost("/keywords/refresh", async (
            HttpContext context,
            IConfiguration configuration,
            CrmRepository repository,
            IKeywordRefreshDispatcher refreshDispatcher,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            var run = await repository.CreateKeywordRefreshRunAsync(
                "manual",
                null,
                actor?.Id,
                actor?.LoginName,
                int.TryParse(configuration["KEYWORD_REFRESH_WINDOW_DAYS"], out var configuredWindowDays) ? configuredWindowDays : 14,
                cancellationToken);
            await refreshDispatcher.QueueAsync(run.Id, cancellationToken);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "keyword_refresh_manual", "keyword_refresh_run", run.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { run.RequestedWindowDays }, cancellationToken);
            return Results.Accepted($"/api/keywords/refresh-status", run);
        });

        keywordGroup.MapPost("/keywords", async (
            HttpContext context,
            KeywordRuleUpsertRequest request,
            CrmRepository repository,
            IConfiguration configuration,
            IKeywordRefreshDispatcher refreshDispatcher,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateKeywordRuleRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var keyword = await repository.UpsertKeywordRuleAsync(null, request, cancellationToken);
            if (keyword is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar la palabra clave.");
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "keyword_create", "keyword", keyword.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { keyword.Keyword, keyword.RuleType, keyword.Scope }, cancellationToken);
            var refreshRun = await repository.CreateKeywordRefreshRunAsync(
                "keyword_create",
                keyword.Id,
                actor?.Id,
                actor?.LoginName,
                int.TryParse(configuration["KEYWORD_REFRESH_WINDOW_DAYS"], out var createdWindowDays) ? createdWindowDays : 14,
                cancellationToken);
            await refreshDispatcher.QueueAsync(refreshRun.Id, cancellationToken);
            return Results.Created($"/api/keywords/{keyword.Id}", keyword);
        });

        keywordGroup.MapPut("/keywords/{id:long}", async (
            HttpContext context,
            long id,
            KeywordRuleUpsertRequest request,
            CrmRepository repository,
            IConfiguration configuration,
            IKeywordRefreshDispatcher refreshDispatcher,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateKeywordRuleRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var keyword = await repository.UpsertKeywordRuleAsync(id, request, cancellationToken);
            if (keyword is null)
            {
                return Results.NotFound();
            }

            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "keyword_update", "keyword", keyword.Id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { keyword.Keyword, keyword.RuleType, keyword.Scope }, cancellationToken);
            var refreshRun = await repository.CreateKeywordRefreshRunAsync(
                "keyword_update",
                keyword.Id,
                actor?.Id,
                actor?.LoginName,
                int.TryParse(configuration["KEYWORD_REFRESH_WINDOW_DAYS"], out var updatedWindowDays) ? updatedWindowDays : 14,
                cancellationToken);
            await refreshDispatcher.QueueAsync(refreshRun.Id, cancellationToken);
            return Results.Ok(keyword);
        });

        keywordGroup.MapDelete("/keywords/{id:long}", async (
            HttpContext context,
            long id,
            CrmRepository repository,
            IConfiguration configuration,
            IKeywordRefreshDispatcher refreshDispatcher,
            CancellationToken cancellationToken) =>
        {
            var deleted = await repository.DeleteKeywordRuleAsync(id, cancellationToken);
            if (deleted)
            {
                var actor = EndpointContext.GetActor(context);
                await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "keyword_delete", "keyword", id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { }, cancellationToken);
                var refreshRun = await repository.CreateKeywordRefreshRunAsync(
                    "keyword_delete",
                    null,
                    actor?.Id,
                    actor?.LoginName,
                    int.TryParse(configuration["KEYWORD_REFRESH_WINDOW_DAYS"], out var deletedWindowDays) ? deletedWindowDays : 14,
                    cancellationToken);
                await refreshDispatcher.QueueAsync(refreshRun.Id, cancellationToken);
            }

            return deleted ? Results.NoContent() : Results.NotFound();
        });

        adminGroup.MapGet("/sercop/credentials", async (
            SercopCredentialVault credentialVault,
            SercopAuthenticatedClient sercopAuthenticatedClient,
            CancellationToken cancellationToken) =>
        {
            var status = await credentialVault.GetStatusAsync(cancellationToken);
            var session = sercopAuthenticatedClient.GetSessionSnapshot();
            return Results.Ok(status with
            {
                PortalSessionStatus = session.PortalSessionStatus,
                LastPortalLoginAt = session.LastPortalLoginAt,
            });
        });

        adminGroup.MapPut("/sercop/credentials", async (
            HttpContext context,
            SercopCredentialsUpsertRequest request,
            SercopCredentialVault credentialVault,
            SercopAuthenticatedClient sercopAuthenticatedClient,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateSercopCredentialsRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var actor = EndpointContext.GetActor(context);
            await credentialVault.SaveAsync(request.Ruc, request.UserName, request.Password, actor?.Id, actor?.LoginName, cancellationToken);
            sercopAuthenticatedClient.ClearSession();
            var validation = await sercopAuthenticatedClient.ValidateStoredCredentialAsync(forceReauthenticate: true, cancellationToken);
            var status = await credentialVault.GetStatusAsync(cancellationToken);
            var session = sercopAuthenticatedClient.GetSessionSnapshot();
            var response = status with
            {
                PortalSessionStatus = session.PortalSessionStatus,
                LastPortalLoginAt = session.LastPortalLoginAt,
            };
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "sercop_credentials_upsert", "sercop_credentials", "portal", EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new
            {
                response.MaskedRuc,
                response.MaskedUserName,
                response.ValidationStatus,
                validation.IsSuccess,
            }, cancellationToken);
            return Results.Ok(response);
        });

        adminGroup.MapPost("/sercop/credentials/test", async (
            HttpContext context,
            SercopCredentialVault credentialVault,
            SercopAuthenticatedClient sercopAuthenticatedClient,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var currentStatus = await credentialVault.GetStatusAsync(cancellationToken);
            if (!currentStatus.Configured)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "No hay credenciales SERCOP configuradas.");
            }

            var validation = await sercopAuthenticatedClient.ValidateStoredCredentialAsync(forceReauthenticate: true, cancellationToken);
            var status = await credentialVault.GetStatusAsync(cancellationToken);
            var session = sercopAuthenticatedClient.GetSessionSnapshot();
            var response = status with
            {
                PortalSessionStatus = session.PortalSessionStatus,
                LastPortalLoginAt = session.LastPortalLoginAt,
            };
            var actor = EndpointContext.GetActor(context);
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "sercop_credentials_test", "sercop_credentials", "portal", EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new
            {
                response.MaskedRuc,
                response.MaskedUserName,
                response.ValidationStatus,
                validation.IsSuccess,
            }, cancellationToken);
            return Results.Ok(response);
        });

        adminGroup.MapDelete("/sercop/credentials", async (
            HttpContext context,
            SercopCredentialVault credentialVault,
            SercopAuthenticatedClient sercopAuthenticatedClient,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            await credentialVault.ClearAsync(cancellationToken);
            sercopAuthenticatedClient.ClearSession();
            await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "sercop_credentials_clear", "sercop_credentials", "portal", EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { }, cancellationToken);
            return Results.NoContent();
        });

        return app;
    }
}
