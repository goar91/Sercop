using System.Security.Claims;

namespace backend.Auth;

internal static class CrmClaimTypes
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string LoginName = ClaimTypes.Name;
    public const string FullName = "crm:full_name";
    public const string ZoneId = "crm:zone_id";
    public const string ZoneName = "crm:zone_name";
}

internal sealed record AuthenticatedCrmUser(
    long Id,
    string LoginName,
    string FullName,
    string Email,
    string Role,
    long? ZoneId,
    string? ZoneName
);

internal static class CrmRoleRules
{
    public static bool IsSeller(AuthenticatedCrmUser? actor)
        => string.Equals(actor?.Role, "seller", StringComparison.OrdinalIgnoreCase);

    public static bool CanManageCommercialAssignments(AuthenticatedCrmUser? actor)
        => actor?.Role is "admin" or "gerencia" or "coordinator";
}

internal static class CrmUserContext
{
    public static AuthenticatedCrmUser? GetCurrentUser(HttpContext httpContext)
    {
        var principal = httpContext.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var idRaw = principal.FindFirstValue(CrmClaimTypes.UserId);
        if (!long.TryParse(idRaw, out var id))
        {
            return null;
        }

        var zoneIdRaw = principal.FindFirstValue(CrmClaimTypes.ZoneId);
        _ = long.TryParse(zoneIdRaw, out var zoneIdValue);

        return new AuthenticatedCrmUser(
            id,
            principal.FindFirstValue(CrmClaimTypes.LoginName) ?? string.Empty,
            principal.FindFirstValue(CrmClaimTypes.FullName) ?? principal.Identity?.Name ?? string.Empty,
            principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty,
            principal.FindFirstValue(ClaimTypes.Role) ?? string.Empty,
            string.IsNullOrWhiteSpace(zoneIdRaw) ? null : zoneIdValue,
            principal.FindFirstValue(CrmClaimTypes.ZoneName));
    }
}
