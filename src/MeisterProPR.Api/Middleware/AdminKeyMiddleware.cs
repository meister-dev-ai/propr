using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Middleware;

/// <summary>
///     Resolves the caller identity and sets <c>context.Items["IsAdmin"]</c> and
///     <c>context.Items["UserId"]</c> by evaluating three credential paths in order:
///     <list type="number">
///       <item><description><c>Authorization: Bearer {jwt}</c> — validated locally.</description></item>
///       <item><description><c>X-User-Pat</c> header — BCrypt-verified against stored PAT hashes.</description></item>
///       <item><description><c>X-Admin-Key</c> — legacy shared-secret; logs a deprecation warning.</description></item>
///     </list>
/// </summary>
public sealed class AdminKeyMiddleware(
    RequestDelegate next,
    IConfiguration configuration,
    ILogger<AdminKeyMiddleware> logger)
{
    private const string AdminKeyHeader = "X-Admin-Key";
    private const string PatHeader = "X-User-Pat";

    /// <inheritdoc cref="IMiddleware.InvokeAsync" />
    public async Task InvokeAsync(HttpContext context)
    {
        // Default: not authenticated as admin
        context.Items["IsAdmin"] = false;

        // 1. JWT Bearer token
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
        {
            var token = authHeader["Bearer ".Length..].Trim();
            var jwtService = context.RequestServices.GetService<IJwtTokenService>();
            if (jwtService is not null)
            {
                var principal = jwtService.ValidateAccessToken(token);
                if (principal is not null)
                {
                    var sub = principal.FindFirst("sub")?.Value;
                    var role = principal.FindFirst("global_role")?.Value;
                    if (!string.IsNullOrEmpty(sub))
                    {
                        context.Items["UserId"] = sub;
                        context.Items["IsAdmin"] = role == AppUserRole.Admin.ToString();
                    }

                    await next(context);
                    return;
                }
            }
        }

        // 2. Personal Access Token (X-User-Pat)
        var pat = context.Request.Headers[PatHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(pat))
        {
            var patRepo = context.RequestServices.GetService<IUserPatRepository>();
            var userRepo = context.RequestServices.GetService<IUserRepository>();
            if (patRepo is not null && userRepo is not null)
            {
                var patEntity = await patRepo.GetActiveByRawTokenAsync(pat);
                if (patEntity is not null)
                {
                    var user = await userRepo.GetByIdAsync(patEntity.UserId);
                    if (user is not null && user.IsActive)
                    {
                        context.Items["UserId"] = user.Id.ToString();
                        context.Items["IsAdmin"] = user.GlobalRole == AppUserRole.Admin;
                        await next(context);
                        return;
                    }
                }
            }
        }

        // 3. Legacy X-Admin-Key (deprecated)
        var adminKey = configuration["MEISTER_ADMIN_KEY"];
        if (!string.IsNullOrWhiteSpace(adminKey))
        {
            var providedKey = context.Request.Headers[AdminKeyHeader].FirstOrDefault();
            var isValid = !string.IsNullOrWhiteSpace(providedKey) && providedKey == adminKey;
            if (isValid)
            {
                logger.LogWarning(
                    "X-Admin-Key is deprecated and will be removed in a future release. " +
                    "Migrate to username/password login (/auth/login) or personal access tokens.");
                context.Items["IsAdmin"] = true;
            }
        }

        await next(context);
    }
}
