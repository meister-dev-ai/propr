// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Claims;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>
///     Resolves the caller identity by evaluating two credential paths in order:
///     <list type="number">
///         <item>
///             <description><c>Authorization: Bearer {jwt}</c> — validated locally.</description>
///         </item>
///         <item>
///             <description><c>X-User-Pat</c> header — verified against stored PAT hashes.</description>
///         </item>
///     </list>
///     On success it populates <c>context.Items["IsAdmin"]</c>, <c>["UserId"]</c>, <c>["ClientRoles"]</c>, and
///     <c>["TenantRoles"]</c> (the contract <see cref="MeisterProPR.Api.Extensions.AuthHelpers" /> reads) and
///     yields an authenticated <see cref="ClaimsPrincipal" /> so framework authorization can see the caller.
/// </summary>
public static class CallerIdentityResolver
{
    private const string PatHeader = "X-User-Pat";

    /// <summary>
    ///     Resolves the caller and populates the per-request authorization context. Always writes the
    ///     default (empty) role maps first so downstream helpers observe a consistent shape.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    /// <param name="authenticationScheme">Scheme name stamped on any principal this resolver mints.</param>
    public static async Task<CallerIdentityResolution> ResolveAsync(HttpContext context, string authenticationScheme)
    {
        context.Items["IsAdmin"] = false;
        context.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>();
        context.Items["TenantRoles"] = new Dictionary<Guid, TenantRole>();

        var bearer = await TryAuthenticateBearerAsync(context);
        if (bearer is not null)
        {
            return bearer.Value;
        }

        var pat = await TryAuthenticatePatAsync(context, authenticationScheme);
        if (pat is not null)
        {
            return pat.Value;
        }

        return CallerIdentityResolution.Anonymous;
    }

    private static async Task<CallerIdentityResolution?> TryAuthenticateBearerAsync(HttpContext context)
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var jwtService = context.RequestServices.GetService<IJwtTokenService>();
        if (jwtService is null)
        {
            return null;
        }

        var principal = jwtService.ValidateAccessToken(token);
        if (principal is null)
        {
            return null;
        }

        var sub = principal.FindFirst("sub")?.Value;
        var role = principal.FindFirst("global_role")?.Value;
        if (string.IsNullOrEmpty(sub))
        {
            return CallerIdentityResolution.ForPrincipal(principal);
        }

        context.Items["UserId"] = sub;
        context.Items["IsAdmin"] = role == AppUserRole.Admin.ToString();

        if (!Guid.TryParse(sub, out var userId))
        {
            return CallerIdentityResolution.ForPrincipal(principal);
        }

        var userRepo = context.RequestServices.GetService<IUserRepository>();
        if (userRepo is null)
        {
            return CallerIdentityResolution.ForPrincipal(principal);
        }

        var user = await userRepo.GetByIdWithAssignmentsAsync(userId, context.RequestAborted);

        // A still-valid access token must not outlive a disabled account: the
        // refresh-token and PAT paths already reject disabled users, and this
        // closes the same gap for bearer JWTs.
        if (user is not null && !user.IsActive)
        {
            return CallerIdentityResolution.DisabledAccount;
        }

        var explicitClientRoles = user is not null
            ? CreateExplicitClientRoles(user)
            : await userRepo.GetUserClientRolesAsync(userId, context.RequestAborted);

        var tenantRoles = user is not null
            ? CreateTenantRoles(user)
            : new Dictionary<Guid, TenantRole>();

        context.Items["ClientRoles"] =
            await BuildEffectiveClientRolesAsync(context, explicitClientRoles, tenantRoles, context.RequestAborted);

        context.Items["TenantRoles"] = tenantRoles;

        return CallerIdentityResolution.ForPrincipal(principal);
    }

    private static async Task<CallerIdentityResolution?> TryAuthenticatePatAsync(
        HttpContext context,
        string authenticationScheme)
    {
        var pat = context.Request.Headers[PatHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(pat))
        {
            return null;
        }

        var patRepo = context.RequestServices.GetService<IUserPatRepository>();
        var userRepo = context.RequestServices.GetService<IUserRepository>();
        if (patRepo is null || userRepo is null)
        {
            return null;
        }

        var patEntity = await patRepo.GetActiveByRawTokenAsync(pat, context.RequestAborted);
        if (patEntity is null)
        {
            return null;
        }

        var user = await userRepo.GetByIdWithAssignmentsAsync(patEntity.UserId, context.RequestAborted);
        if (user is not null && user.IsActive)
        {
            context.Items["UserId"] = user.Id.ToString();
            context.Items["IsAdmin"] = user.GlobalRole == AppUserRole.Admin;
            var tenantRoles = CreateTenantRoles(user);
            var explicitClientRoles = CreateExplicitClientRoles(user);
            context.Items["ClientRoles"] =
                await BuildEffectiveClientRolesAsync(context, explicitClientRoles, tenantRoles, context.RequestAborted);
            context.Items["TenantRoles"] = tenantRoles;
            return CallerIdentityResolution.ForPrincipal(BuildPatPrincipal(user, authenticationScheme));
        }

        // Defense in depth: PATs are revoked when a user is disabled, so this should not
        // normally fire, but reject explicitly if a live PAT ever outlives the account.
        if (user is not null && !user.IsActive)
        {
            return CallerIdentityResolution.DisabledAccount;
        }

        return null;
    }

    private static ClaimsPrincipal BuildPatPrincipal(AppUser user, string authenticationScheme)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", user.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim("global_role", user.GlobalRole.ToString()),
            ],
            authenticationScheme);
        return new ClaimsPrincipal(identity);
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
        var visibleClientsQuery = dbContext.Clients
            .AsNoTracking()
            .Where(client => explicitClientIds.Contains(client.Id) || tenantIds.Contains(client.TenantId));

        if (isCommunityEdition)
        {
            visibleClientsQuery = visibleClientsQuery.Where(client => client.TenantId == Guid.Empty || client.TenantId == TenantCatalog.SystemTenantId);
        }

        var visibleClients = await visibleClientsQuery
            .Select(client => new { client.Id, client.TenantId })
            .ToListAsync(ct);

        var visibleClientIds = visibleClients.Select(client => client.Id).ToHashSet();
        var effectiveRoles = explicitClientRoles
            .Where(entry => !visibleClientIds.Contains(entry.Key))
            .ToDictionary();

        foreach (var client in visibleClients)
        {
            ApplyEffectiveClientRole(effectiveRoles, client.Id, client.TenantId, explicitClientRoles, tenantRoles);
        }

        return effectiveRoles;
    }

    private static void ApplyEffectiveClientRole(
        Dictionary<Guid, ClientRole> effectiveRoles,
        Guid clientId,
        Guid clientTenantId,
        IReadOnlyDictionary<Guid, ClientRole> explicitClientRoles,
        IReadOnlyDictionary<Guid, TenantRole> tenantRoles)
    {
        if (explicitClientRoles.TryGetValue(clientId, out var explicitRole)
            && (clientTenantId == Guid.Empty
                || TenantCatalog.IsSystemTenant(clientTenantId)
                || tenantRoles.ContainsKey(clientTenantId)))
        {
            effectiveRoles[clientId] = explicitRole;
        }

        if (!tenantRoles.TryGetValue(clientTenantId, out var tenantRole))
        {
            return;
        }

        var derivedRole = tenantRole >= TenantRole.TenantAdministrator
            ? ClientRole.ClientAdministrator
            : ClientRole.ClientUser;

        if (!effectiveRoles.TryGetValue(clientId, out var currentRole) || currentRole < derivedRole)
        {
            effectiveRoles[clientId] = derivedRole;
        }
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

/// <summary>
///     Outcome of <see cref="CallerIdentityResolver.ResolveAsync" />: an authenticated principal,
///     an anonymous request, or a recognized-but-disabled account.
/// </summary>
public readonly record struct CallerIdentityResolution
{
    private CallerIdentityResolution(ClaimsPrincipal? principal, bool accountDisabled)
    {
        this.Principal = principal;
        this.AccountDisabled = accountDisabled;
    }

    /// <summary>No usable credential was presented.</summary>
    public static CallerIdentityResolution Anonymous => new(null, false);

    /// <summary>A credential resolved to a user whose account is disabled; the request must be rejected.</summary>
    public static CallerIdentityResolution DisabledAccount => new(null, true);

    /// <summary>The authenticated principal, or <see langword="null" /> when anonymous or disabled.</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>True when a credential matched a disabled account.</summary>
    public bool AccountDisabled { get; }

    /// <summary>Wraps an authenticated principal as a successful resolution.</summary>
    public static CallerIdentityResolution ForPrincipal(ClaimsPrincipal principal)
    {
        return new CallerIdentityResolution(principal, false);
    }
}
