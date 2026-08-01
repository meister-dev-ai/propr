// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Extensions;

/// <summary>
///     Chooses a CORS policy by the caller's origin, so a browser extension can reach the API without
///     being handed the credentials the admin UI relies on.
/// </summary>
/// <remarks>
///     <para>
///         The default policy allows credentials, because the admin UI authenticates partly by cookie —
///         the refresh token is one. Adding extension origins to that policy would let <em>any</em>
///         installed extension make a credentialed request carrying that cookie and read the response,
///         and extension origins cannot be enumerated to narrow it: Firefox randomises
///         <c>moz-extension://</c> per installation, so the match can only be by scheme.
///     </para>
///     <para>
///         Extension callers therefore get a policy that permits the request and refuses credentials.
///         The browser extension authenticates with an <c>X-User-Pat</c> header and deliberately sends no
///         cookies, so it loses nothing; an extension trying to ride the session cookie gains nothing.
///     </para>
///     <para>
///         Chromium exempts an extension's own requests from cross-origin checks when it holds the host
///         permission, so none of this is needed there. Firefox enforces them on the extension's
///         background worker as it would on any page, which is why the extension works in one browser and
///         not the other without this.
///     </para>
///     <para>
///         None of this is an access control. A token holder can call the API from any HTTP client; this
///         decides only which browser origins may read a response.
///     </para>
/// </remarks>
internal sealed class BrowserCorsPolicyProvider : ICorsPolicyProvider
{
    private static readonly CorsPolicy ExtensionPolicy = BuildExtensionPolicy();

    private readonly DefaultCorsPolicyProvider fallback;

    /// <summary>Initializes the provider over the application's configured CORS options.</summary>
    /// <param name="options">
    ///     The configured options, passed through so every non-extension caller keeps the default policy
    ///     exactly as registered. Building a fresh set here would silently empty it.
    /// </param>
    public BrowserCorsPolicyProvider(IOptions<CorsOptions> options)
    {
        this.fallback = new DefaultCorsPolicyProvider(options);
    }

    /// <inheritdoc />
    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        ArgumentNullException.ThrowIfNull(context);

        var origin = context.Request.Headers.Origin.FirstOrDefault();

        if (!string.IsNullOrEmpty(origin) && BrowserOriginPolicy.IsExtensionOrigin(origin))
        {
            return Task.FromResult<CorsPolicy?>(ExtensionPolicy);
        }

        return this.fallback.GetPolicyAsync(context, policyName);
    }

    private static CorsPolicy BuildExtensionPolicy()
    {
        // Deliberately no AllowCredentials. That omission is the entire reason this policy exists.
        return new CorsPolicyBuilder()
            .SetIsOriginAllowed(BrowserOriginPolicy.IsExtensionOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .Build();
    }
}
