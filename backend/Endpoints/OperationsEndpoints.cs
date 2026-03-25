using backend.Auth;

namespace backend.Endpoints;

internal static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        var commercialGroup = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Commercial);
        var adminGroup = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Operations);

        commercialGroup.MapGet("/zones", async (CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var zones = await repository.GetZonesAsync(cancellationToken);
            return Results.Ok(zones);
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

        adminGroup.MapPost("/zones", async (ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateZoneRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var zone = await repository.UpsertZoneAsync(null, request, cancellationToken);
            return zone is null ? Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar la zona.") : Results.Created($"/api/zones/{zone.Id}", zone);
        });

        adminGroup.MapPut("/zones/{id:long}", async (long id, ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateZoneRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var zone = await repository.UpsertZoneAsync(id, request, cancellationToken);
            return zone is null ? Results.NotFound() : Results.Ok(zone);
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

        adminGroup.MapGet("/keywords", async (
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

        adminGroup.MapPost("/keywords", async (
            HttpContext context,
            KeywordRuleUpsertRequest request,
            CrmRepository repository,
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
            return Results.Created($"/api/keywords/{keyword.Id}", keyword);
        });

        adminGroup.MapPut("/keywords/{id:long}", async (
            HttpContext context,
            long id,
            KeywordRuleUpsertRequest request,
            CrmRepository repository,
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
            return Results.Ok(keyword);
        });

        adminGroup.MapDelete("/keywords/{id:long}", async (
            HttpContext context,
            long id,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var deleted = await repository.DeleteKeywordRuleAsync(id, cancellationToken);
            if (deleted)
            {
                var actor = EndpointContext.GetActor(context);
                await repository.WriteAuditLogAsync(actor?.Id, actor?.LoginName, "keyword_delete", "keyword", id.ToString(), EndpointContext.GetClientIp(context), EndpointContext.GetUserAgent(context), new { }, cancellationToken);
            }

            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}
