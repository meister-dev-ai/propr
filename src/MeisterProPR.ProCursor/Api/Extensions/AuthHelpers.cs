// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Api.Extensions;

/// <summary>
///     Helpers for per-client role enforcement using values populated by the API auth middleware.
/// </summary>
public static class AuthHelpers
{
    private static readonly IReadOnlyDictionary<Guid, ClientRole> EmptyClientRoles = new Dictionary<Guid, ClientRole>();

    /// <summary>Returns <see langword="true" /> when the current caller is a global admin.</summary>
    public static bool IsAdmin(HttpContext ctx)
    {
        return ctx.Items["IsAdmin"] is true;
    }

    /// <summary>Returns the authenticated user ID when present and parseable; otherwise <see langword="null" />.</summary>
    public static Guid? GetUserId(HttpContext ctx)
    {
        return ctx.Items["UserId"] is string value && Guid.TryParse(value, out var userId)
            ? userId
            : null;
    }

    /// <summary>Returns the current per-client role map, or an empty map when the caller has no scoped roles.</summary>
    public static IReadOnlyDictionary<Guid, ClientRole> GetClientRoles(HttpContext ctx)
    {
        return ctx.Items["ClientRoles"] as Dictionary<Guid, ClientRole> ?? EmptyClientRoles;
    }

    /// <summary>
    ///     Returns <c>null</c> when the caller is a global admin or holds at least
    ///     <paramref name="minRole"/> for the specified <paramref name="clientId"/>.
    /// </summary>
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
        if (roles.TryGetValue(clientId, out var role) && role >= minRole)
        {
            return null;
        }

        return CreateError(StatusCodes.Status403Forbidden, "You do not have the required role for this client.");
    }

    private static ObjectResult CreateError(int statusCode, string message)
    {
        return new ObjectResult(new { error = message })
        {
            StatusCode = statusCode,
        };
    }
}
