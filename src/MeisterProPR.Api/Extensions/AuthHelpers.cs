// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Extensions;

/// <summary>
///     Helpers for per-client role enforcement using <c>HttpContext.Items["ClientRoles"]</c>
///     plus tenant role enforcement using <c>HttpContext.Items["TenantRoles"]</c>, all populated by
///     <see cref="MeisterProPR.Api.Middleware.AuthMiddleware" />.
/// </summary>
public static class AuthHelpers
{
    private static readonly IReadOnlyDictionary<Guid, ClientRole> EmptyClientRoles = new Dictionary<Guid, ClientRole>();
    private static readonly IReadOnlyDictionary<Guid, TenantRole> EmptyTenantRoles = new Dictionary<Guid, TenantRole>();

    /// <summary>Returns <see langword="true" /> when the current caller is a global admin.</summary>
    public static bool IsAdmin(HttpContext ctx)
    {
        return ctx.Items["IsAdmin"] is true;
    }

    /// <summary>Returns the authenticated user ID when present and parseable; otherwise <see langword="null" />.</summary>
    public static Guid? GetUserId(HttpContext ctx)
    {
        return ctx.Items["UserId"] is string s && Guid.TryParse(s, out var userId)
            ? userId
            : null;
    }

    /// <summary>Returns the current per-client role map, or an empty map when the caller has no scoped roles.</summary>
    public static IReadOnlyDictionary<Guid, ClientRole> GetClientRoles(HttpContext ctx)
    {
        return ctx.Items["ClientRoles"] as Dictionary<Guid, ClientRole> ?? EmptyClientRoles;
    }

    /// <summary>Returns the current per-tenant role map, or an empty map when the caller has no tenant memberships.</summary>
    public static IReadOnlyDictionary<Guid, TenantRole> GetTenantRoles(HttpContext ctx)
    {
        return ctx.Items["TenantRoles"] as Dictionary<Guid, TenantRole> ?? EmptyTenantRoles;
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is authenticated as either a global admin or a normal user.
    ///     Returns a 401 object result otherwise.
    /// </summary>
    public static IActionResult? RequireAuthenticated(HttpContext ctx)
    {
        return IsAdmin(ctx) || GetUserId(ctx).HasValue
            ? null
            : CreateError(StatusCodes.Status401Unauthorized, "Valid credentials required.");
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is a global admin.
    ///     Returns 403 for authenticated non-admin callers and 401 for unauthenticated callers.
    /// </summary>
    public static IActionResult? RequireAdmin(HttpContext ctx)
    {
        if (IsAdmin(ctx))
        {
            return null;
        }

        return GetUserId(ctx).HasValue
            ? CreateError(StatusCodes.Status403Forbidden, "Admin role required.")
            : CreateError(StatusCodes.Status401Unauthorized, "Valid credentials required.");
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is a platform administrator.
    ///     Tenant-scoped administrators are intentionally excluded from this path.
    /// </summary>
    public static IActionResult? RequirePlatformAdmin(HttpContext ctx)
    {
        return RequireAdmin(ctx);
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is a global admin or holds at least one client role
    ///     at or above <paramref name="minRole" />.
    /// </summary>
    public static IActionResult? RequireAnyClientRole(HttpContext ctx, ClientRole minRole)
    {
        if (IsAdmin(ctx))
        {
            return null;
        }

        if (!GetUserId(ctx).HasValue)
        {
            return CreateError(StatusCodes.Status401Unauthorized, "Authentication required.");
        }

        return GetClientRoles(ctx).Values.Any(role => role >= minRole)
            ? null
            : CreateError(StatusCodes.Status403Forbidden, "You do not have the required role for this operation.");
    }

    /// <summary>
    ///     Returns <c>null</c> (access granted) when the caller is a global admin or holds at least
    ///     <paramref name="minRole" /> for the specified <paramref name="clientId" />.
    ///     Returns a 403 object result otherwise.
    /// </summary>
    /// <param name="ctx">The current HTTP context.</param>
    /// <param name="clientId">The client identifier to check the role against.</param>
    /// <param name="minRole">Minimum required role (e.g. <see cref="ClientRole.ClientAdministrator" />).</param>
    public static IActionResult? RequireClientRole(HttpContext ctx, Guid clientId, ClientRole minRole)
    {
        if (IsAdmin(ctx))
        {
            return null;
        }

        if (!GetUserId(ctx).HasValue)
        {
            return CreateError(StatusCodes.Status401Unauthorized, "Authentication required.");
        }

        var roles = GetClientRoles(ctx);
        if (roles.TryGetValue(clientId, out var role) &&
            role >= minRole)
        {
            return null;
        }

        return CreateError(StatusCodes.Status403Forbidden, "You do not have the required role for this client.");
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is a global admin or holds at least one tenant role at or above
    ///     <paramref name="minRole" />.
    /// </summary>
    public static IActionResult? RequireAnyTenantRole(HttpContext ctx, TenantRole minRole)
    {
        if (IsAdmin(ctx))
        {
            return null;
        }

        if (!GetUserId(ctx).HasValue)
        {
            return CreateError(StatusCodes.Status401Unauthorized, "Authentication required.");
        }

        return GetTenantRoles(ctx).Values.Any(role => role >= minRole)
            ? null
            : CreateError(StatusCodes.Status403Forbidden, "You do not have the required tenant role for this operation.");
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is a global admin or holds at least <paramref name="minRole" />
    ///     for the specified <paramref name="tenantId" />.
    /// </summary>
    public static IActionResult? RequireTenantRole(HttpContext ctx, Guid tenantId, TenantRole minRole)
    {
        if (IsAdmin(ctx))
        {
            return null;
        }

        if (!GetUserId(ctx).HasValue)
        {
            return CreateError(StatusCodes.Status401Unauthorized, "Authentication required.");
        }

        var roles = GetTenantRoles(ctx);
        if (roles.TryGetValue(tenantId, out var role) && role >= minRole)
        {
            return null;
        }

        return CreateError(StatusCodes.Status403Forbidden, "You do not have the required role for this tenant.");
    }

    /// <summary>Maps the shared issued-session contract to the public tenant-auth response payload.</summary>
    public static TenantAuthSessionDto ToTenantAuthSessionDto(IssuedSession session)
    {
        return new TenantAuthSessionDto(session.AccessToken, session.RefreshToken, session.ExpiresIn, session.TokenType);
    }

    private static ObjectResult CreateError(int statusCode, string message)
    {
        return new ObjectResult(new { error = message })
        {
            StatusCode = statusCode,
        };
    }
}
