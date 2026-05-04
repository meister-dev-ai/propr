// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Api.Middleware;

/// <summary>
///     Resolves the caller identity and sets <c>context.Items["IsAdmin"]</c>,
///     <c>context.Items["UserId"]</c>, <c>context.Items["ClientRoles"]</c>, and
///     <c>context.Items["TenantRoles"]</c>
///     by evaluating two credential paths in order:
///     <list type="number">
///         <item>
///             <description><c>Authorization: Bearer {jwt}</c> — validated locally.</description>
///         </item>
///         <item>
///             <description><c>X-User-Pat</c> header — BCrypt-verified against stored PAT hashes.</description>
///         </item>
///     </list>
/// </summary>
public sealed class AuthMiddleware(RequestDelegate next)
{
    private const string PatHeader = "X-User-Pat";

    /// <inheritdoc cref="IMiddleware.InvokeAsync" />
    public async Task InvokeAsync(HttpContext context)
    {
        context.Items["IsAdmin"] = false;
        context.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>();
        context.Items["TenantRoles"] = new Dictionary<Guid, TenantRole>();

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
                                var user = await userRepo.GetByIdWithAssignmentsAsync(userId, context.RequestAborted);
                                var explicitClientRoles = user is not null
                                    ? CreateExplicitClientRoles(user)
                                    : await userRepo.GetUserClientRolesAsync(userId, context.RequestAborted);

                                var tenantRoles = user is not null
                                    ? CreateTenantRoles(user)
                                    : new Dictionary<Guid, TenantRole>();

                                context.Items["ClientRoles"] =
                                    await BuildEffectiveClientRolesAsync(
                                        context,
                                        explicitClientRoles,
                                        tenantRoles,
                                        context.RequestAborted);

                                context.Items["TenantRoles"] = tenantRoles;
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
                var patEntity = await patRepo.GetActiveByRawTokenAsync(pat, context.RequestAborted);
                if (patEntity is not null)
                {
                    var user = await userRepo.GetByIdWithAssignmentsAsync(patEntity.UserId, context.RequestAborted);
                    if (user is not null && user.IsActive)
                    {
                        context.Items["UserId"] = user.Id.ToString();
                        context.Items["IsAdmin"] = user.GlobalRole == AppUserRole.Admin;
                        var tenantRoles = CreateTenantRoles(user);
                        var explicitClientRoles = CreateExplicitClientRoles(user);
                        context.Items["ClientRoles"] =
                            await BuildEffectiveClientRolesAsync(
                                context,
                                explicitClientRoles,
                                tenantRoles,
                                context.RequestAborted);
                        context.Items["TenantRoles"] = tenantRoles;
                        await next(context);
                        return;
                    }
                }
            }
        }

        await next(context);
    }

    private static Dictionary<Guid, ClientRole> CreateExplicitClientRoles(AppUser user)
    {
        return user.ClientAssignments.ToDictionary(assignment => assignment.ClientId, assignment => assignment.Role);
    }

    private static Dictionary<Guid, TenantRole> CreateTenantRoles(AppUser user)
    {
        return user.TenantMemberships.ToDictionary(membership => membership.TenantId, membership => membership.Role);
    }

    private static async Task<Dictionary<Guid, ClientRole>> BuildEffectiveClientRolesAsync(
        HttpContext context,
        IReadOnlyDictionary<Guid, ClientRole> explicitClientRoles,
        IReadOnlyDictionary<Guid, TenantRole> tenantRoles,
        CancellationToken ct)
    {
        explicitClientRoles ??= new Dictionary<Guid, ClientRole>();
        tenantRoles ??= new Dictionary<Guid, TenantRole>();

        if (explicitClientRoles.Count == 0 && tenantRoles.Count == 0)
        {
            return new Dictionary<Guid, ClientRole>();
        }

        var dbContext = context.RequestServices.GetService<MeisterProPRDbContext>();
        if (dbContext is null)
        {
            return new Dictionary<Guid, ClientRole>(explicitClientRoles);
        }

        var explicitClientIds = explicitClientRoles.Keys.ToArray();
        var tenantIds = tenantRoles.Keys.ToArray();
        var isCommunityEdition = await IsCommunityEditionAsync(context, ct);
        var visibleClients = await dbContext.Clients
            .AsNoTracking()
            .Where(client => explicitClientIds.Contains(client.Id) || tenantIds.Contains(client.TenantId))
            .Where(client => TenantCatalog.IsClientVisible(client.TenantId, isCommunityEdition))
            .Select(client => new { client.Id, client.TenantId })
            .ToListAsync(ct);

        var visibleClientIds = visibleClients.Select(client => client.Id).ToHashSet();
        var effectiveRoles = explicitClientRoles
            .Where(entry => !visibleClientIds.Contains(entry.Key))
            .ToDictionary();

        foreach (var client in visibleClients)
        {
            if (explicitClientRoles.TryGetValue(client.Id, out var explicitRole)
                && (client.TenantId == Guid.Empty
                    || TenantCatalog.IsSystemTenant(client.TenantId)
                    || tenantRoles.ContainsKey(client.TenantId)))
            {
                effectiveRoles[client.Id] = explicitRole;
            }

            if (!tenantRoles.TryGetValue(client.TenantId, out var tenantRole))
            {
                continue;
            }

            var derivedRole = tenantRole >= TenantRole.TenantAdministrator
                ? ClientRole.ClientAdministrator
                : ClientRole.ClientUser;

            if (!effectiveRoles.TryGetValue(client.Id, out var currentRole) || currentRole < derivedRole)
            {
                effectiveRoles[client.Id] = derivedRole;
            }
        }

        return effectiveRoles;
    }

    private static async Task<bool> IsCommunityEditionAsync(HttpContext context, CancellationToken ct)
    {
        var licensingCapabilityService = context.RequestServices.GetService<ILicensingCapabilityService>();
        if (licensingCapabilityService is null)
        {
            return false;
        }

        var summaryTask = licensingCapabilityService.GetSummaryAsync(ct);
        if (summaryTask is null)
        {
            return false;
        }

        var summary = await summaryTask;
        return summary?.Edition == InstallationEdition.Community;
    }
}
