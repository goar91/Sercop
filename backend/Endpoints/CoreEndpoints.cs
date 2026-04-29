using backend.Auth;
using System.Text;

namespace backend.Endpoints;

internal static class CoreEndpoints
{
    public static IEndpointRouteBuilder MapCoreEndpoints(this IEndpointRouteBuilder app, bool hasFrontend, string frontendDist)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            service = "sercop-crm-api",
            timestamp = EcuadorTime.Now(),
            frontend = hasFrontend ? "served" : "missing-build"
        })).AllowAnonymous();

        var apiGroup = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Authenticated);

        apiGroup.MapGet("/meta", (HttpContext context, IConfiguration configuration) =>
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

        apiGroup.MapGet("/dashboard", async (HttpContext context, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(context);
            var summary = await repository.GetDashboardAsync(actor, cancellationToken);
            return Results.Ok(summary);
        }).RequireAuthorization(CrmPolicies.Authenticated);

        app.MapFallback(async context =>
        {
            if (!hasFrontend)
            {
                context.Response.Redirect("/api/health");
                return;
            }

            await ServeSpaIndexAsync(context, frontendDist);
        });

        return app;
    }

    private static async Task ServeSpaIndexAsync(HttpContext context, string frontendDist)
    {
        var indexPath = Path.Combine(frontendDist, "index.html");
        var html = await File.ReadAllTextAsync(indexPath, Encoding.UTF8, context.RequestAborted);

        var pathBase = context.Request.PathBase.HasValue
            ? context.Request.PathBase.Value!.TrimEnd('/') + "/"
            : "/";

        html = html.Replace("<base href=\"/\">", $"<base href=\"{pathBase}\">", StringComparison.Ordinal);

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }
}
