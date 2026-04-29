using System.Text.Json.Serialization;
using backend;
using backend.Auth;
using backend.Endpoints;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Serilog;
using Serilog.Events;
using System.Globalization;
using System.Net;
using NetIPNetwork = System.Net.IPNetwork;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

var dataProtectionKeysDir = ResolveDataProtectionKeysDirectory(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataProtectionKeysDir);

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
builder.Services
    .AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysDir))
    .SetApplicationName("HDM-CRM");
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
    options.AddPolicy(CrmPolicies.KeywordManagers, policy => policy.RequireRole("admin", "coordinator"));
    options.AddPolicy(CrmPolicies.Automation, policy => policy.RequireRole("admin", "analyst"));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        if (!context.HttpContext.Response.HasStarted)
        {
            context.HttpContext.Response.ContentType = "application/problem+json";
            await Results.Problem(
                title: "Demasiadas solicitudes",
                detail: "Espera un momento antes de volver a intentar la operacion.",
                statusCode: StatusCodes.Status429TooManyRequests).ExecuteAsync(context.HttpContext);
        }
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/swagger"))
        {
            return RateLimitPartition.GetNoLimiter("static-ui");
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 50,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.AddPolicy("auth-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(10),
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
builder.Services.AddSingleton<SercopCredentialVault>();
builder.Services.AddSingleton<ISercopCredentialStore>(serviceProvider => serviceProvider.GetRequiredService<SercopCredentialVault>());
builder.Services.AddSingleton<SercopAuthenticatedClient>();
builder.Services.AddSingleton<KeywordRefreshBackgroundService>();
builder.Services.AddSingleton<IKeywordRefreshDispatcher>(serviceProvider => serviceProvider.GetRequiredService<KeywordRefreshBackgroundService>());
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
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<KeywordRefreshBackgroundService>());
builder.Services.AddHostedService<OpportunityRetentionCleanupBackgroundService>();

var app = builder.Build();

var frontendCandidates = new[]
{
    Path.Combine(app.Environment.ContentRootPath, "..", "frontend", "dist", "frontend", "browser"),
    Path.Combine(app.Environment.ContentRootPath, "frontend", "dist", "frontend", "browser"),
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "frontend", "dist", "frontend", "browser")
}.Select(Path.GetFullPath).ToArray();
var frontendDist = frontendCandidates.FirstOrDefault(Directory.Exists) ?? frontendCandidates[0];
var hasFrontend = Directory.Exists(frontendDist);
var optionalPathBase = NormalizePathBase(app.Configuration["CRM_PATH_BASE"]);

var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
foreach (var proxy in GetTrustedProxies(app.Configuration))
{
    forwardedHeadersOptions.KnownProxies.Add(proxy);
}

foreach (var network in GetTrustedNetworks(app.Configuration))
{
    forwardedHeadersOptions.KnownIPNetworks.Add(network);
}

app.UseForwardedHeaders(forwardedHeadersOptions);

if (!string.IsNullOrWhiteSpace(optionalPathBase))
{
    app.Use((context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(optionalPathBase, out var remaining))
        {
            context.Request.PathBase = context.Request.PathBase.Add(optionalPathBase);
            context.Request.Path = remaining;
        }

        return next();
    });
}

app.UseRouting();

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

app.Use(async (context, next) =>
{
    ApplySecurityHeaders(context);
    await next();
});

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
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        RequestPath = string.Empty,
        OnPrepareResponse = context =>
        {
            if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.ContentType = "text/html; charset=utf-8";
                context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Context.Response.Headers.Pragma = "no-cache";
                context.Context.Response.Headers.Expires = "0";
                return;
            }

            context.Context.Response.Headers.CacheControl = "public,max-age=3600";
        }
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

static void ApplySecurityHeaders(HttpContext context)
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = BuildContentSecurityPolicy(context.Request.Path);

    if (!IsLocalRequest(context) && context.Request.IsHttps)
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
}

static string BuildContentSecurityPolicy(PathString path)
{
    if (path.StartsWithSegments("/swagger"))
    {
        return "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; img-src 'self' data:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self';";
    }

    return "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; img-src 'self' data: https:; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self' data: https://fonts.gstatic.com; connect-src 'self' https://fonts.googleapis.com https://fonts.gstatic.com;";
}

static IReadOnlyList<IPAddress> GetTrustedProxies(IConfiguration configuration)
{
    var proxies = new List<IPAddress>();
    foreach (var value in SplitSetting(configuration["CRM_TRUSTED_PROXIES"]))
    {
        if (IPAddress.TryParse(value, out var address))
        {
            proxies.Add(address);
        }
    }

    return proxies;
}

static IReadOnlyList<NetIPNetwork> GetTrustedNetworks(IConfiguration configuration)
{
    var configuredNetworks = new List<NetIPNetwork>();
    foreach (var value in SplitSetting(configuration["CRM_TRUSTED_NETWORKS"]))
    {
        if (TryParseIpNetwork(value, out var network))
        {
            configuredNetworks.Add(network);
        }
    }

    if (configuredNetworks.Count > 0)
    {
        return configuredNetworks;
    }

    return new[]
    {
        NetIPNetwork.Parse("127.0.0.0/8"),
        NetIPNetwork.Parse("::1/128"),
        NetIPNetwork.Parse("10.0.0.0/8"),
        NetIPNetwork.Parse("172.16.0.0/12"),
        NetIPNetwork.Parse("192.168.0.0/16"),
        NetIPNetwork.Parse("100.64.0.0/10")
    };
}

static string ResolveDataProtectionKeysDirectory(IConfiguration configuration, string contentRootPath)
{
    var configured = configuration["CRM_DATA_PROTECTION_KEYS_DIR"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
    }

    var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    if (!string.IsNullOrWhiteSpace(commonAppData))
    {
        return Path.Combine(commonAppData, "HDM-CRM", "keys");
    }

    return Path.Combine(contentRootPath, "run", "data-protection-keys");
}

static IEnumerable<string> SplitSetting(string? value)
    => string.IsNullOrWhiteSpace(value)
        ? Array.Empty<string>()
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static bool TryParseIpNetwork(string rawValue, out NetIPNetwork network)
{
    network = default;
    try
    {
        network = NetIPNetwork.Parse(rawValue);
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

static string? NormalizePathBase(string? rawValue)
{
    if (string.IsNullOrWhiteSpace(rawValue))
    {
        return null;
    }

    var normalized = rawValue.Trim();
    if (!normalized.StartsWith('/'))
    {
        normalized = "/" + normalized;
    }

    normalized = normalized.TrimEnd('/');
    return string.Equals(normalized, "/", StringComparison.Ordinal) ? null : normalized;
}
