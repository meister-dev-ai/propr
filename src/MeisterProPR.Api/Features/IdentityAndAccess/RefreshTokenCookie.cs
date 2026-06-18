// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     Centralises the httpOnly refresh-token cookie used to keep a browser session valid
///     across tabs without exposing the refresh token to JavaScript. The access token stays a
///     short-lived bearer token; the SPA re-establishes its session on load via /auth/refresh,
///     which reads this cookie. Path is "/" so it survives any "/api" reverse-proxy prefix
///     stripping; Secure tracks the request scheme so http dev still works.
/// </summary>
public static class RefreshTokenCookie
{
    public const string Name = "meisterpropr_refresh";

    public static void Set(HttpResponse response, string rawToken, DateTimeOffset expiresAt, bool isHttps)
    {
        response.Cookies.Append(Name, rawToken, BuildOptions(isHttps, expiresAt));
    }

    public static void Clear(HttpResponse response, bool isHttps)
    {
        response.Cookies.Append(Name, string.Empty, BuildOptions(isHttps, DateTimeOffset.UnixEpoch));
    }

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
