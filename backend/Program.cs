using System.Text.Json.Serialization;
using backend;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using System.Net.Mail;
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

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var loggerFactory = context.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("GlobalException");
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        logger.LogError(feature?.Error, "Unhandled exception for {Path}", context.Request.Path);

        if (feature?.Error is PostgresException postgresException)
        {
            var (statusCode, title, detail) = MapPostgresException(postgresException);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/problem+json";

            await Results.Problem(
                title: title,
                detail: detail,
                statusCode: statusCode).ExecuteAsync(context);
            return;
        }

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
    timestamp = EcuadorTime.Now(),
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

app.MapGet("/api/management/report", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var report = await repository.GetManagementReportAsync(cancellationToken);
    return Results.Ok(report);
});

app.MapGet("/api/opportunities", async (
    string? search,
    string? estado,
    long? zoneId,
    long? assignedUserId,
    bool? invitedOnly,
    bool? todayOnly,
    CrmRepository repository,
    CancellationToken cancellationToken) =>
{
    var items = await repository.GetOpportunitiesAsync(search, estado, zoneId, assignedUserId, invitedOnly ?? false, todayOnly ?? true, cancellationToken);
    return Results.Ok(items);
});

app.MapGet("/api/opportunities/{id:long}", async (long id, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var detail = await repository.GetOpportunityAsync(id, cancellationToken);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapPut("/api/opportunities/{id:long}/assignment", async (long id, OpportunityAssignmentRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = await ValidateAssignmentRequestAsync(request, repository, cancellationToken);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

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
    var errors = ValidateInvitationUpdateRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

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
    var errors = ValidateZoneRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var zone = await repository.UpsertZoneAsync(null, request, cancellationToken);
    return zone is null ? Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar la zona.") : Results.Created($"/api/zones/{zone.Id}", zone);
});

app.MapPut("/api/zones/{id:long}", async (long id, ZoneUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = ValidateZoneRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var zone = await repository.UpsertZoneAsync(id, request, cancellationToken);
    return zone is null ? Results.NotFound() : Results.Ok(zone);
});

app.MapGet("/api/users", async (CrmRepository repository, CancellationToken cancellationToken) =>
{
    var users = await repository.GetUsersAsync(cancellationToken);
    return Results.Ok(users);
});

app.MapPost("/api/users", async (UserUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = await ValidateUserRequestAsync(request, repository, cancellationToken);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var user = await repository.UpsertUserAsync(null, request, cancellationToken);
    return user is null ? Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar el usuario.") : Results.Created($"/api/users/{user.Id}", user);
});

app.MapPut("/api/users/{id:long}", async (long id, UserUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = await ValidateUserRequestAsync(request, repository, cancellationToken);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var user = await repository.UpsertUserAsync(id, request, cancellationToken);
    return user is null ? Results.NotFound() : Results.Ok(user);
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
    return keyword is null ? Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "No se pudo guardar la palabra clave.") : Results.Created($"/api/keywords/{keyword.Id}", keyword);
});

app.MapPut("/api/keywords/{id:long}", async (long id, KeywordRuleUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var errors = ValidateKeywordRuleRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var keyword = await repository.UpsertKeywordRuleAsync(id, request, cancellationToken);
    return keyword is null ? Results.NotFound() : Results.Ok(keyword);
});

app.MapDelete("/api/keywords/{id:long}", async (long id, CrmRepository repository, CancellationToken cancellationToken) =>
{
    var deleted = await repository.DeleteKeywordRuleAsync(id, cancellationToken);
    return deleted ? Results.NoContent() : Results.NotFound();
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

static Dictionary<string, string[]> ValidateZoneRequest(ZoneUpsertRequest request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        errors["name"] = ["El nombre de la zona es obligatorio."];
    }

    if (string.IsNullOrWhiteSpace(request.Code))
    {
        errors["code"] = ["El codigo de la zona es obligatorio."];
    }
    else
    {
        var normalizedCode = request.Code.Trim().ToUpperInvariant();
        if (normalizedCode.Length is < 2 or > 12)
        {
            errors["code"] = ["El codigo debe tener entre 2 y 12 caracteres."];
        }
    }

    return errors;
}

static async Task<Dictionary<string, string[]>> ValidateUserRequestAsync(UserUpsertRequest request, CrmRepository repository, CancellationToken cancellationToken)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.FullName))
    {
        errors["fullName"] = ["El nombre completo es obligatorio."];
    }

    if (string.IsNullOrWhiteSpace(request.Email))
    {
        errors["email"] = ["El correo es obligatorio."];
    }
    else if (!IsValidEmail(request.Email))
    {
        errors["email"] = ["El correo no tiene un formato valido."];
    }

    var normalizedRole = request.Role?.Trim().ToLowerInvariant();
    if (normalizedRole is not ("manager" or "seller" or "analyst"))
    {
        errors["role"] = ["El rol debe ser manager, seller o analyst."];
    }

    if (request.ZoneId.HasValue && !await repository.ZoneExistsAsync(request.ZoneId.Value, cancellationToken))
    {
        errors["zoneId"] = ["La zona seleccionada no existe."];
    }

    return errors;
}

static async Task<Dictionary<string, string[]>> ValidateAssignmentRequestAsync(OpportunityAssignmentRequest request, CrmRepository repository, CancellationToken cancellationToken)
{
    var errors = new Dictionary<string, string[]>();

    if (!string.IsNullOrWhiteSpace(request.Estado))
    {
        var normalizedStatus = request.Estado.Trim().ToLowerInvariant();
        if (normalizedStatus is not ("nuevo" or "en_revision" or "asignado" or "presentado" or "ganado" or "perdido" or "no_presentado"))
        {
            errors["estado"] = ["El estado no es valido."];
        }
    }

    if (!string.IsNullOrWhiteSpace(request.Priority))
    {
        var normalizedPriority = request.Priority.Trim().ToLowerInvariant();
        if (normalizedPriority is not ("alta" or "normal" or "baja"))
        {
            errors["priority"] = ["La prioridad debe ser alta, normal o baja."];
        }
    }

    if (request.AssignedUserId.HasValue && !await repository.UserExistsAsync(request.AssignedUserId.Value, cancellationToken))
    {
        errors["assignedUserId"] = ["El vendedor seleccionado no existe."];
    }

    if (request.ZoneId.HasValue && !await repository.ZoneExistsAsync(request.ZoneId.Value, cancellationToken))
    {
        errors["zoneId"] = ["La zona seleccionada no existe."];
    }

    return errors;
}

static Dictionary<string, string[]> ValidateInvitationUpdateRequest(OpportunityInvitationUpdateRequest request)
{
    var errors = new Dictionary<string, string[]>();

    if (request.IsInvitedMatch && string.IsNullOrWhiteSpace(request.InvitationSource))
    {
        errors["invitationSource"] = ["Debes indicar la fuente de la invitacion confirmada."];
    }

    return errors;
}

static bool IsValidEmail(string email)
{
    try
    {
        _ = new MailAddress(email.Trim());
        return true;
    }
    catch
    {
        return false;
    }
}

static (int StatusCode, string Title, string Detail) MapPostgresException(PostgresException exception)
{
    return exception.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation => (
            StatusCodes.Status409Conflict,
            "Conflicto de datos",
            "Ya existe un registro con esos valores unicos. Revisa correo, nombre o codigo."),
        PostgresErrorCodes.CheckViolation or PostgresErrorCodes.NotNullViolation or PostgresErrorCodes.StringDataRightTruncation => (
            StatusCodes.Status400BadRequest,
            "Datos invalidos",
            "La base de datos rechazo la operacion por valores incompletos o fuera de formato."),
        PostgresErrorCodes.ForeignKeyViolation => (
            StatusCodes.Status400BadRequest,
            "Relacion invalida",
            "La operacion referencia una zona, usuario o proceso que no existe."),
        _ => (
            StatusCodes.Status500InternalServerError,
            "Error interno del CRM",
            "La solicitud no se pudo completar. Revisa el backend o los datos de PostgreSQL."),
    };
}


