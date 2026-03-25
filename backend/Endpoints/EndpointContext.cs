using backend.Auth;

namespace backend.Endpoints;

internal static class EndpointContext
{
    public static AuthenticatedCrmUser? GetActor(HttpContext context)
        => CrmUserContext.GetCurrentUser(context);

    public static string? GetClientIp(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString();

    public static string? GetUserAgent(HttpContext context)
        => context.Request.Headers.UserAgent.ToString();
}
