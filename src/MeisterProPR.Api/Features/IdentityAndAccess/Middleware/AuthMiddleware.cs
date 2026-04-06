// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Middleware;

/// <summary>
///     Resolves the caller identity and sets <c>context.Items["IsAdmin"]</c>,
///     <c>context.Items["UserId"]</c>, and <c>context.Items["ClientRoles"]</c>
///     by evaluating two credential paths in order:
///     <list type="number">
///       <item><description><c>Authorization: Bearer {jwt}</c> — validated locally.</description></item>
///       <item><description><c>X-User-Pat</c> header — BCrypt-verified against stored PAT hashes.</description></item>
///     </list>
/// </summary>
public sealed class AuthMiddleware(
    RequestDelegate next)
{
    private const string PatHeader = "X-User-Pat";

    /// <inheritdoc cref="IMiddleware.InvokeAsync" />
    public async Task InvokeAsync(HttpContext context)
    {
        context.Items["IsAdmin"] = false;

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

                        if (Guid.TryParse(sub, out var userId))
                        {
                            var userRepo = context.RequestServices.GetService<IUserRepository>();
                            if (userRepo is not null)
                            {
                                context.Items["ClientRoles"] = await userRepo.GetUserClientRolesAsync(userId, context.RequestAborted);
                            }
                        }
                    }

                    await next(context);
                    return;
                }
            }
        }

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
                        context.Items["ClientRoles"] = await userRepo.GetUserClientRolesAsync(user.Id, context.RequestAborted);
                        await next(context);
                        return;
                    }
                }
            }
        }

        await next(context);
    }
}
