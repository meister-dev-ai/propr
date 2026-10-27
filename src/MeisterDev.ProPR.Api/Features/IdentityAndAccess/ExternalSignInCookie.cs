// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     An httpOnly cookie that holds the protected state of a sign-in through an external identity provider from one
///     request of a browser to a later one. ProPR completes such a step only in the browser that holds the cookie.
/// </summary>
/// <remarks>
///     <c>SameSite=Lax</c> keeps the cookie on the top-level navigation from the provider back to ProPR, and keeps it off
///     requests that pages of other sites send. The path is <c>/</c>, so the cookie also reaches the API behind a reverse
///     proxy that strips a prefix such as <c>/api</c>.
/// </remarks>
public sealed class ExternalSignInCookie
{
    /// <summary>
    ///     The state, the nonce and the PKCE code verifier of a sign-in of a platform administrator, from the redirect to
    ///     the provider until the provider's callback.
    /// </summary>
    public static readonly ExternalSignInCookie PlatformAdminFlow = new("meisterpropr_platform_admin_sign_in", TimeSpan.FromMinutes(15));

    /// <summary>
    ///     The state, the nonce and the PKCE code verifier of a tenant sign-in, from the redirect to the provider until the
    ///     provider's callback.
    /// </summary>
    public static readonly ExternalSignInCookie TenantFlow = new("meisterpropr_tenant_sign_in", TimeSpan.FromMinutes(15));

    /// <summary>
    ///     A tenant sign-in whose identity waits for the confirmation of its link to an account, or for a new account.
    /// </summary>
    public static readonly ExternalSignInCookie PendingTenantLink = new("meisterpropr_tenant_sign_in_link", TimeSpan.FromMinutes(15));

    private ExternalSignInCookie(string name, TimeSpan lifetime)
    {
        this.Name = name;
        this.Lifetime = lifetime;
    }

    /// <summary>The name of the cookie.</summary>
    public string Name { get; }

    /// <summary>How long the browser keeps the cookie.</summary>
    public TimeSpan Lifetime { get; }

    /// <summary>Stores the protected value.</summary>
    public void Set(HttpResponse response, string protectedValue, bool isHttps)
    {
        response.Cookies.Append(this.Name, protectedValue, BuildOptions(isHttps, DateTimeOffset.UtcNow + this.Lifetime));
    }

    /// <summary>Removes the cookie.</summary>
    public void Clear(HttpResponse response, bool isHttps)
    {
        response.Cookies.Append(this.Name, string.Empty, BuildOptions(isHttps, DateTimeOffset.UnixEpoch));
    }

    /// <summary>Returns the protected value, or <see langword="null" /> when the browser sent none.</summary>
    public string? Read(HttpRequest request)
    {
        return request.Cookies.TryGetValue(this.Name, out var value) && !string.IsNullOrWhiteSpace(value)
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
