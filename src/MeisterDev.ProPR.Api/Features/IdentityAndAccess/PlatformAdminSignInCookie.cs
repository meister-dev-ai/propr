// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     The httpOnly cookie that holds the protected state of a sign-in through the identity provider for platform
///     administrators, from the redirect to the provider until the provider's callback. The callback completes a
///     sign-in only in the browser that started it, because only that browser holds the cookie with the state, the
///     nonce and the PKCE code verifier.
/// </summary>
/// <remarks>
///     <c>SameSite=Lax</c> keeps the cookie on the top-level navigation from the provider back to ProPR. The path
///     is <c>/</c>, so the cookie also reaches the callback behind a reverse proxy that strips a prefix such as
///     <c>/api</c>.
/// </remarks>
public static class PlatformAdminSignInCookie
{
    /// <summary>The name of the cookie.</summary>
    public const string Name = "meisterpropr_platform_admin_sign_in";

    /// <summary>How long a sign-in may take at the provider before the state expires.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>Stores the protected state of a sign-in that is about to start.</summary>
    public static void Set(HttpResponse response, string protectedFlow, bool isHttps)
    {
        response.Cookies.Append(Name, protectedFlow, BuildOptions(isHttps, DateTimeOffset.UtcNow + Lifetime));
    }

    /// <summary>Removes the cookie, which the callback does whatever the outcome of the sign-in.</summary>
    public static void Clear(HttpResponse response, bool isHttps)
    {
        response.Cookies.Append(Name, string.Empty, BuildOptions(isHttps, DateTimeOffset.UnixEpoch));
    }

    /// <summary>Returns the protected state, or <see langword="null" /> when the browser sent none.</summary>
    public static string? Read(HttpRequest request)
    {
        return request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static CookieOptions BuildOptions(bool isHttps, DateTimeOffset expires)
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = expires,
            IsEssential = true,
        };
    }
}
