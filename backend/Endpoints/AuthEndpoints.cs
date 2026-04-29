using System.Security.Claims;
using backend.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace backend.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", async (
            HttpContext httpContext,
            LoginRequestDto request,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var errors = EndpointValidation.ValidateLoginRequest(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var user = await repository.AuthenticateAsync(request.Identifier, request.Password, cancellationToken);
            if (user is null)
            {
                await repository.WriteAuditLogAsync(
                    null,
                    null,
                    "login_failed",
                    "auth",
                    null,
                    EndpointContext.GetClientIp(httpContext),
                    EndpointContext.GetUserAgent(httpContext),
                    new { request.Identifier },
                    cancellationToken);

                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Credenciales invalidas");
            }

            var claims = new List<Claim>
            {
                new(CrmClaimTypes.UserId, user.Id.ToString()),
                new(CrmClaimTypes.LoginName, user.LoginName),
                new(CrmClaimTypes.FullName, user.FullName),
                new(ClaimTypes.Email, user.Email),
                new(ClaimTypes.Role, user.Role),
            };

            if (user.ZoneId.HasValue)
            {
                claims.Add(new Claim(CrmClaimTypes.ZoneId, user.ZoneId.Value.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(user.ZoneName))
            {
                claims.Add(new Claim(CrmClaimTypes.ZoneName, user.ZoneName));
            }

            var identity = new ClaimsIdentity(claims, CrmCookieDefaults.Scheme);
            var principal = new ClaimsPrincipal(identity);
            await httpContext.SignInAsync(
                CrmCookieDefaults.Scheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = request.RememberMe,
                    AllowRefresh = true,
                    ExpiresUtc = request.RememberMe ? DateTimeOffset.UtcNow.AddDays(14) : DateTimeOffset.UtcNow.AddHours(10)
                });

            await repository.WriteAuditLogAsync(
                user.Id,
                user.LoginName,
                "login_success",
                "auth",
                user.Id.ToString(),
                EndpointContext.GetClientIp(httpContext),
                EndpointContext.GetUserAgent(httpContext),
                new { request.RememberMe },
                cancellationToken);

            return Results.Ok(new LoginResponseDto(user, "Sesion iniciada."));
        }).AllowAnonymous().RequireRateLimiting("auth-login");

        group.MapPost("/logout", async (
            HttpContext httpContext,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(httpContext);
            await httpContext.SignOutAsync(CrmCookieDefaults.Scheme);

            await repository.WriteAuditLogAsync(
                actor?.Id,
                actor?.LoginName,
                "logout",
                "auth",
                actor?.Id.ToString(),
                EndpointContext.GetClientIp(httpContext),
                EndpointContext.GetUserAgent(httpContext),
                new { },
                cancellationToken);

            return Results.Ok(new { message = "Sesion cerrada." });
        }).RequireAuthorization(CrmPolicies.Authenticated);

        group.MapGet("/me", async (
            HttpContext httpContext,
            CrmRepository repository,
            CancellationToken cancellationToken) =>
        {
            var actor = EndpointContext.GetActor(httpContext);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var currentUser = await repository.GetCurrentUserAsync(actor.Id, cancellationToken);
            return currentUser is null ? Results.Unauthorized() : Results.Ok(currentUser);
        }).RequireAuthorization(CrmPolicies.Authenticated);

        return app;
    }
}
