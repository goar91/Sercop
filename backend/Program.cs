using System.Text.Json.Serialization;
using System.Text.Json;
using backend;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.WriteIndented = false;
});

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200", "http://127.0.0.1:4200", "http://localhost:5050", "http://127.0.0.1:5050"];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddSingleton(_ =>
{
    var configuration = builder.Configuration;
    var connectionStringBuilder = new NpgsqlConnectionStringBuilder
    {
        Host = configuration["CRM_DB_HOST"] ?? configuration["Database:Host"] ?? "localhost",
        Port = int.TryParse(configuration["CRM_DB_PORT"] ?? configuration["Database:Port"], out var port) ? port : 5432,
        Database = configuration["POSTGRES_DB"] ?? configuration["Database:Name"] ?? "sercop_crm",
        Username = configuration["POSTGRES_USER"] ?? configuration["Database:User"] ?? "sercop_local",
        Password = configuration["POSTGRES_PASSWORD"] ?? configuration["Database:Password"] ?? string.Empty,
        Pooling = true,
        IncludeErrorDetail = true,
        Timeout = 15,
        CommandTimeout = 30
    };

    return new NpgsqlDataSourceBuilder(connectionStringBuilder.ConnectionString).Build();
});

builder.Services.AddScoped<CrmRepository>();
builder.Services.AddHttpClient<SercopPublicClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HDM-SERCOP-CRM/1.0");
});
builder.Services.AddHttpClient<SercopInvitationPublicClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HDM-SERCOP-CRM/1.0");
});
builder.Services.AddHttpClient<PersonalAssistantService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(180);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("SERCOP-Personal-AI/1.0");
});
builder.Services.AddScoped<PersonalAssistantDocumentService>();
builder.Services.AddHttpClient<StudioService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(300);
});
builder.Services.AddSingleton<VideoRenderService>();
builder.Services.AddHostedService<PublicInvitationSyncService>();

var app = builder.Build();
var frontendCandidates = new[]
{
    Path.Combine(app.Environment.ContentRootPath, "..", "frontend", "dist", "frontend", "browser"),
    Path.Combine(app.Environment.ContentRootPath, "frontend", "dist", "frontend", "browser"),
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "frontend", "dist", "frontend", "browser")
}.Select(Path.GetFullPath).ToArray();
var frontendDist = frontendCandidates.FirstOrDefault(Directory.Exists) ?? frontendCandidates[0];
var hasFrontend = Directory.Exists(frontendDist);
var personalAssistantPage = Path.Combine(app.Environment.ContentRootPath, "backend", "PersonalAssistantPage.html");
var personalAssistantUploadRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "backups", "personal-ai", "uploads"));
Directory.CreateDirectory(personalAssistantUploadRoot);

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("GlobalException");
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        logger.LogError(feature?.Error, "Unhandled exception for {Path}", context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/problem+json";

        await Results.Problem(
            title: "Error interno del CRM",
            detail: "La solicitud no se pudo completar. Revisa el backend o los datos de PostgreSQL.",
            statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(context);
    });
});

app.UseResponseCompression();
app.UseCors();

var crmAuthActive = string.Equals(app.Configuration["CRM_BASIC_AUTH_ACTIVE"], "true", StringComparison.OrdinalIgnoreCase);
var crmAuthUser = app.Configuration["CRM_BASIC_AUTH_USER"] ?? "admin";
var crmAuthPassword = app.Configuration["CRM_BASIC_AUTH_PASSWORD"] ?? string.Empty;

app.Use(async (context, next) =>
{
    if (!crmAuthActive || context.Request.Path.StartsWithSegments("/api/health"))
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue("Authorization", out var authorizationHeader))
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"CRM\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var headerValue = authorizationHeader.ToString();
    if (!headerValue.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"CRM\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    try
    {
        var encoded = headerValue[6..].Trim();
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        var separatorIndex = decoded.IndexOf(':');
        var user = separatorIndex >= 0 ? decoded[..separatorIndex] : decoded;
        var password = separatorIndex >= 0 ? decoded[(separatorIndex + 1)..] : string.Empty;

        if (!string.Equals(user, crmAuthUser, StringComparison.Ordinal) || !string.Equals(password, crmAuthPassword, StringComparison.Ordinal))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"CRM\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    catch
    {
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"CRM\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    await next();
});


if (hasFrontend)
{
    var fileProvider = new PhysicalFileProvider(frontendDist);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = fileProvider,
        RequestPath = string.Empty
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = string.Empty,
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=3600";
        }
    });
}

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    service = "sercop-crm-api",
    timestamp = DateTimeOffset.UtcNow,
    frontend = hasFrontend ? "served" : "missing-build"
}));

app.MapGet("/api/meta", (HttpContext context, IConfiguration configuration) =>
{
    var configuredN8nUrl = configuration["N8N_EDITOR_BASE_URL"];
    var n8nEditorUrl = !string.IsNullOrWhiteSpace(configuredN8nUrl) && !configuredN8nUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
        ? configuredN8nUrl
        : $"{context.Request.Scheme}://{context.Request.Host.Host}:5678/";

    return Results.Ok(new MetaDto(
        n8nEditorUrl,
        "PostgreSQL local + CRM",
        configuration["POSTGRES_DB"] ?? configuration["Database:Name"] ?? "sercop_crm",
        configuration["RESPONSIBLE_EMAIL"] ?? configuration["Notifications:ResponsibleEmail"],
        configuration["INVITED_COMPANY_NAME"] ?? "HDM"
    ));
});

app.MapGet("/api/dashboard", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var summary = await repository.GetDashboardAsync(cancellationToken);
    return Results.Ok(summary);
});

app.MapGet("/api/opportunities", async (
    string? search,
    string? estado,
    long? zoneId,
    long? assignedUserId,
    bool invitedOnly,
    CrmRepository repository,
    CancellationToken cancellationToken) =>
{
    var items = await repository.GetOpportunitiesAsync(search, estado, zoneId, assignedUserId, invitedOnly, cancellationToken);
    return Results.Ok(items);
});

app.MapGet("/api/opportunities/{id:long}", async (long id, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var detail = await repository.GetOpportunityAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPut("/api/opportunities/{id:long}/assignment", async (long id, OpportunityAssignmentRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var detail = await repository.UpdateAssignmentAsync(id, request, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPut("/api/opportunities/{id:long}/invitation", async (
    long id,
    OpportunityInvitationUpdateRequest request,
    IConfiguration configuration,
    CrmRepository repository,
    CancellationToken cancellationToken) =>
{
    var invitedCompanyName = configuration["INVITED_COMPANY_NAME"] ?? "HDM";
    var detail = await repository.UpdateInvitationAsync(id, request, invitedCompanyName, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPost("/api/invitations/import", async (
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
    return Results.Ok(result);
});

app.MapPost("/api/invitations/verify-codes", async (
    InvitationCodeVerificationRequest request,
    IConfiguration configuration,
    CrmRepository repository,
    SercopPublicClient sercopPublicClient,
    SercopInvitationPublicClient invitationClient,
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
    var invitedCompanyRuc = configuration["INVITED_COMPANY_RUC"];
    var fallbackYear = int.TryParse(configuration["OCDS_YEAR"], out var configuredYear) ? configuredYear : DateTime.UtcNow.Year;
    var result = await repository.VerifyInvitationCodesAsync(
        request,
        sercopPublicClient,
        invitationClient,
        invitedCompanyName,
        invitedCompanyRuc,
        fallbackYear,
        cancellationToken);

    return Results.Ok(result);
});

app.MapPost("/api/invitations/sync", async (
    IConfiguration configuration,
    CrmRepository repository,
    SercopInvitationPublicClient invitationClient,
    CancellationToken cancellationToken) =>
{
    var invitedCompanyName = configuration["INVITED_COMPANY_NAME"] ?? "HDM";
    var invitedCompanyRuc = configuration["INVITED_COMPANY_RUC"];
    var result = await repository.SyncInvitationsFromPublicReportsAsync(invitationClient, invitedCompanyName, invitedCompanyRuc, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/api/zones", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var zones = await repository.GetZonesAsync(cancellationToken);
    return Results.Ok(zones);
});

app.MapPost("/api/zones", async (ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var zone = await repository.UpsertZoneAsync(null, request, cancellationToken);
    return Results.Created($"/api/zones/{zone.Id}", zone);
});

app.MapPut("/api/zones/{id:long}", async (long id, ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var zone = await repository.UpsertZoneAsync(id, request, cancellationToken);
    return Results.Ok(zone);
});

app.MapGet("/api/users", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var users = await repository.GetUsersAsync(cancellationToken);
    return Results.Ok(users);
});

app.MapPost("/api/users", async (UserUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var user = await repository.UpsertUserAsync(null, request, cancellationToken);
    return Results.Created($"/api/users/{user.Id}", user);
});

app.MapPut("/api/users/{id:long}", async (long id, UserUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var user = await repository.UpsertUserAsync(id, request, cancellationToken);
    return Results.Ok(user);
});

app.MapGet("/api/keywords", async (
    string? ruleType,
    string? scope,
    CrmRepository repository,
    CancellationToken cancellationToken) =>
{
    var errors = ValidateKeywordRuleFilters(ruleType, scope);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var keywords = await repository.GetKeywordRulesAsync(ruleType, scope, cancellationToken);
    return Results.Ok(keywords);
});

app.MapPost("/api/keywords", async (KeywordRuleUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = ValidateKeywordRuleRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var keyword = await repository.UpsertKeywordRuleAsync(null, request, cancellationToken);
    return Results.Created($"/api/keywords/{keyword.Id}", keyword);
});

app.MapPut("/api/keywords/{id:long}", async (long id, KeywordRuleUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = ValidateKeywordRuleRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var keyword = await repository.UpsertKeywordRuleAsync(id, request, cancellationToken);
    return Results.Ok(keyword);
});

app.MapGet("/api/workflows", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var workflows = await repository.GetWorkflowsAsync(cancellationToken);
    return Results.Ok(workflows);
});

app.MapGet("/api/workflows/{id}", async (string id, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var workflow = await repository.GetWorkflowAsync(id, cancellationToken);
    return workflow is null ? Results.NotFound() : Results.Ok(workflow);
});

app.MapGet("/api/personal-ai/sessions", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var sessions = await repository.GetPersonalAssistantSessionsAsync(cancellationToken);
    return Results.Ok(sessions);
});

app.MapGet("/api/personal-ai/sessions/{id:long}", async (long id, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var session = await repository.GetPersonalAssistantSessionAsync(id, cancellationToken);
    return session is null ? Results.NotFound() : Results.Ok(session);
});

app.MapGet("/api/personal-ai/memory", async (
    string? search,
    int? limit,
    CrmRepository repository,
    CancellationToken cancellationToken) =>
{
    var safeLimit = Math.Clamp(limit ?? 24, 1, 100);
    var memory = await repository.ListPersonalAssistantMemoryAsync(search, safeLimit, cancellationToken);
    return Results.Ok(memory);
});

app.MapPost("/api/personal-ai/ask", async (PersonalAssistantAskRequest request, PersonalAssistantService assistant, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["question"] = ["La pregunta es obligatoria."]
        });
    }

    var reply = await assistant.AskAsync(request, cancellationToken);
    return Results.Ok(reply);
});

app.MapPost("/api/personal-ai/analyze-documents", async (
    HttpRequest httpRequest,
    PersonalAssistantService assistant,
    CancellationToken cancellationToken) =>
{
    var form = await httpRequest.ReadFormAsync(cancellationToken);
    var question = form["question"].ToString();
    var searchMode = form["searchMode"].ToString();
    var sessionIdText = form["sessionId"].ToString();
    var sessionId = long.TryParse(sessionIdText, out var parsedSessionId) ? parsedSessionId : (long?)null;

    if (form.Files.Count == 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["files"] = ["Debes subir al menos un documento."]
        });
    }

    try
    {
        var reply = await assistant.AnalyzeDocumentsAsync(question, sessionId, searchMode, form.Files.ToList(), cancellationToken);
        return Results.Ok(reply);
    }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["files"] = [exception.Message]
        });
    }
});

app.MapGet("/api/personal-ai/uploads/{*relativePath}", (string relativePath) =>
{
    if (string.IsNullOrWhiteSpace(relativePath))
    {
        return Results.NotFound();
    }

    var sanitized = relativePath.Replace('/', Path.DirectorySeparatorChar);
    var absolutePath = Path.GetFullPath(Path.Combine(personalAssistantUploadRoot, sanitized));
    if (!absolutePath.StartsWith(personalAssistantUploadRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(absolutePath))
    {
        return Results.NotFound();
    }

    return Results.File(absolutePath, "application/octet-stream", Path.GetFileName(absolutePath));
});

app.MapPost("/api/assistant/ask", async (AssistantAskRequest request, PersonalAssistantService assistant, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["question"] = ["La pregunta es obligatoria."]
        });
    }

    var reply = await assistant.AskAsync(new PersonalAssistantAskRequest(
        request.Question,
        null,
        "auto",
        request.FilePath,
        request.Language,
        request.Selection,
        request.CodeContext), cancellationToken);

    return Results.Ok(new AssistantReplyDto(
        request.Module ?? "personal",
        reply.Model,
        "Asistente personal desacoplado del CRM.",
        reply.Answer,
        reply.Sources));
});

app.MapGet("/api/studio/assets", async (
    string? assetScope,
    long? opportunityId,
    string? workflowId,
    CrmRepository repository,
    CancellationToken cancellationToken) =>
{
    var scope = NormalizeStudioScope(assetScope, opportunityId, workflowId);
    var assets = await repository.GetStudioAssetsAsync(scope, opportunityId, workflowId, cancellationToken);
    return Results.Ok(assets);
});

app.MapGet("/api/studio/assets/{id:long}", async (long id, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var asset = await repository.GetStudioAssetAsync(id, cancellationToken);
    return asset is null ? Results.NotFound() : Results.Ok(asset);
});

app.MapGet("/api/studio/assets/{id:long}/download", async (long id, CrmRepository repository, IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    var asset = await repository.GetStudioAssetAsync(id, cancellationToken);
    if (asset is null)
    {
        return Results.NotFound();
    }

    if (string.Equals(asset.AssetType, "rendered_video", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            using var payload = JsonDocument.Parse(asset.PayloadJson);
            if (payload.RootElement.TryGetProperty("relativePath", out var pathElement))
            {
                var relativePath = pathElement.GetString();
                if (!string.IsNullOrWhiteSpace(relativePath))
                {
                    var absolutePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                    if (File.Exists(absolutePath))
                    {
                        return Results.File(absolutePath, asset.Format, $"{SanitizeFileName(asset.Title)}.mp4");
                    }
                }
            }
        }
        catch
        {
        }

        return Results.NotFound();
    }

    var extension = asset.Format switch
    {
        "text/markdown" => "md",
        "application/json" => "json",
        "application/x-subrip" => "srt",
        _ => "txt"
    };

    return Results.File(
        System.Text.Encoding.UTF8.GetBytes(asset.ContentText ?? string.Empty),
        asset.Format,
        $"{SanitizeFileName(asset.Title)}.{extension}");
});

app.MapPost("/api/studio/generate", async (StudioGenerateRequest request, StudioService studioService, CancellationToken cancellationToken) =>
{
    var errors = ValidateStudioRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var result = await studioService.GenerateAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/assistant", async (HttpContext context) =>
{
    if (!File.Exists(personalAssistantPage))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("Personal assistant UI not found.");
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(personalAssistantPage);
});

app.MapFallback(async context =>
{
    if (!hasFrontend)
    {
        context.Response.Redirect("/api/health");
        return;
    }

    await context.Response.SendFileAsync(Path.Combine(frontendDist, "index.html"));
});

app.Run();

static Dictionary<string, string[]> ValidateKeywordRuleFilters(string? ruleType, string? scope)
{
    var errors = new Dictionary<string, string[]>();

    if (!string.IsNullOrWhiteSpace(ruleType) && ruleType is not ("include" or "exclude"))
    {
        errors["ruleType"] = ["Debe ser include o exclude."];
    }

    if (!string.IsNullOrWhiteSpace(scope) && scope is not ("all" or "ocds" or "nco"))
    {
        errors["scope"] = ["Debe ser all, ocds o nco."];
    }

    return errors;
}

static Dictionary<string, string[]> ValidateKeywordRuleRequest(KeywordRuleUpsertRequest request)
{
    var errors = ValidateKeywordRuleFilters(request.RuleType, request.Scope);

    if (string.IsNullOrWhiteSpace(request.Keyword))
    {
        errors["keyword"] = ["La palabra clave es obligatoria."];
    }

    if (request.Weight <= 0)
    {
        errors["weight"] = ["El peso debe ser mayor que cero."];
    }

    return errors;
}

static Dictionary<string, string[]> ValidateStudioRequest(StudioGenerateRequest request)
{
    var errors = new Dictionary<string, string[]>();
    var scope = NormalizeStudioScope(request.AssetScope, request.OpportunityId, request.WorkflowId);

    if (scope == "opportunity" && request.OpportunityId is null)
    {
        errors["opportunityId"] = ["Debes seleccionar una oportunidad para este scope."];
    }

    if (scope == "workflow" && string.IsNullOrWhiteSpace(request.WorkflowId))
    {
        errors["workflowId"] = ["Debes seleccionar un workflow para este scope."];
    }

    return errors;
}

static string NormalizeStudioScope(string? scope, long? opportunityId, string? workflowId)
    => scope?.Trim().ToLowerInvariant() switch
    {
        "opportunity" when opportunityId is not null => "opportunity",
        "workflow" when !string.IsNullOrWhiteSpace(workflowId) => "workflow",
        "dashboard" => "dashboard",
        _ when opportunityId is not null => "opportunity",
        _ when !string.IsNullOrWhiteSpace(workflowId) => "workflow",
        _ => "dashboard"
    };

static string SanitizeFileName(string value)
{
    var invalid = Path.GetInvalidFileNameChars();
    var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
    return string.IsNullOrWhiteSpace(safe) ? "asset" : safe;
}


