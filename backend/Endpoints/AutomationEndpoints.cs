using backend.Auth;

namespace backend.Endpoints;

internal static class AutomationEndpoints
{
    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization(CrmPolicies.Automation);

        group.MapGet("/workflows", async (
            int? page,
            int? pageSize,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var workflows = await repository.SearchWorkflowsAsync(page ?? 1, pageSize ?? 25, cancellationToken);
            return Results.Ok(workflows);
        });

        group.MapGet("/workflows/{id}", async (string id, CrmRepository repository, CancellationToken cancellationToken) =>
        {
            var workflow = await repository.GetWorkflowAsync(id, cancellationToken);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        return app;
    }
}
