// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>
///     Constants for the interactive-user authentication scheme that resolves a bearer JWT or
///     <c>X-User-Pat</c> credential into an authenticated <see cref="System.Security.Claims.ClaimsPrincipal" />.
///     This is the application's default scheme; the internal ProCursor shared-key scheme pins itself
///     explicitly on its own endpoints.
/// </summary>
public static class UserAuthenticationDefaults
{
    /// <summary>Authentication scheme name for interactive user (JWT / PAT) requests.</summary>
    public const string Scheme = "MeisterUser";
}
