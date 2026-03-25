using backend.Auth;

namespace backend.Endpoints;

internal static class ManagementEndpoints
{
    public static IEndpointRouteBuilder MapManagementEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Management);

        group.MapGet("/management/report", async (
            string? range,
            long? zoneId,
            long? sellerId,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var report = await repository.GetManagementReportAsync(range, zoneId, sellerId, cancellationToken);
            return Results.Ok(report);
        });

        return app;
    }
}
