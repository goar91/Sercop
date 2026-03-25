using System.Text.Json.Serialization;
using backend;
using backend.Auth;
using backend.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Serilog;
using Serilog.Events;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(AppContext.BaseDirectory, "logs", "crm-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            shared: true);
});

builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options => options.EnableForHttps = true);
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
            .AllowAnyMethod()
            .AllowCredentials());
});

builder.Services.AddAuthentication(CrmCookieDefaults.Scheme)
    .AddCookie(CrmCookieDefaults.Scheme, options =>
    {
        options.Cookie.Name = "hdm.crm.session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        options.LoginPath = "/login";
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/swagger"))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/swagger"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(CrmPolicies.Authenticated, policy => policy.RequireAuthenticatedUser());
    options.AddPolicy(CrmPolicies.Commercial, policy => policy.RequireRole("admin", "gerencia", "coordinator", "seller", "analyst"));
    options.AddPolicy(CrmPolicies.CommercialManagers, policy => policy.RequireRole("admin", "gerencia", "coordinator"));
    options.AddPolicy(CrmPolicies.Management, policy => policy.RequireRole("admin", "gerencia"));
    options.AddPolicy(CrmPolicies.Operations, policy => policy.RequireRole("admin"));
    options.AddPolicy(CrmPolicies.Automation, policy => policy.RequireRole("admin", "analyst"));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 50,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HDM-SERCOP-CRM/2.0");
});
builder.Services.AddHttpClient<SercopInvitationPublicClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HDM-SERCOP-CRM/2.0");
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

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseExceptionHandler(exceptionApp =>
{
    exceptionApp.Run(async context =>
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("GlobalException");
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        logger.LogError(feature?.Error, "Unhandled exception for {Path}", context.Request.Path);

        if (feature?.Error is PostgresException postgresException)
        {
            var (statusCode, title, detail) = EndpointValidation.MapPostgresException(postgresException);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/problem+json";
            await Results.Problem(title: title, detail: detail, statusCode: statusCode).ExecuteAsync(context);
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

app.UseSerilogRequestLogging();
app.UseResponseCompression();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var requireHttps = !string.Equals(app.Configuration["CRM_REQUIRE_HTTPS"], "false", StringComparison.OrdinalIgnoreCase);
app.Use(async (context, next) =>
{
    if (requireHttps && !context.Request.IsHttps && !IsLocalRequest(context))
    {
        var httpsUrl = $"https://{context.Request.Host}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        context.Response.Redirect(httpsUrl, permanent: false);
        return;
    }

    await next();
});

app.UseWhen(context => context.Request.Path.StartsWithSegments("/swagger"), branch =>
{
    branch.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next();
    });
});

app.UseSwagger();
app.UseSwaggerUI();

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
        OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "public,max-age=3600"
    });
}

using (var scope = app.Services.CreateScope())
{
    var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var repository = scope.ServiceProvider.GetRequiredService<CrmRepository>();
    var bootstrapPassword = configuration["CRM_AUTH_BOOTSTRAP_PASSWORD"]
        ?? configuration["CRM_BASIC_AUTH_PASSWORD"]
        ?? "ChangeMe123!";

    await repository.EnsureBootstrapAdminAsync(
        configuration["CRM_ADMIN_LOGIN"] ?? "admin",
        configuration["CRM_ADMIN_NAME"] ?? "Administrador CRM",
        configuration["CRM_ADMIN_EMAIL"] ?? "admin@hdm.local",
        bootstrapPassword,
        CancellationToken.None);
}

app.MapAuthEndpoints();
app.MapCoreEndpoints(hasFrontend, frontendDist);
app.MapManagementEndpoints();
app.MapOpportunityEndpoints();
app.MapOperationsEndpoints();
app.MapAutomationEndpoints();

app.Run();

static bool IsLocalRequest(HttpContext context)
{
    if (context.Connection.RemoteIpAddress is null)
    {
        return true;
    }

    return IPAddress.IsLoopback(context.Connection.RemoteIpAddress)
           || string.Equals(context.Request.Host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(context.Request.Host.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
}
