// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>
///     The redirects of a sign-in through an external identity provider: the redirect of the browser to the provider,
///     and the redirect back to the frontend, which carries the outcome of the sign-in in the URL fragment. Each target
///     is checked before the browser is sent there.
/// </summary>
internal static class ExternalSignInRedirects
{
    /// <summary>
    ///     Returns the address of the provider's sign-in page when it is an absolute <c>http</c> or <c>https</c> URL on
    ///     <paramref name="allowedOrigin" />, the origin of the provider's authorization endpoint.
    /// </summary>
    public static Uri? TryResolveProviderRedirectUri(string? redirectUrl, string allowedOrigin)
    {
        if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var redirectUri) || !IsSupportedRedirectScheme(redirectUri))
        {
            return null;
        }

        var redirectOrigin = redirectUri.GetLeftPart(UriPartial.Authority);
        return string.Equals(redirectOrigin, allowedOrigin, StringComparison.OrdinalIgnoreCase)
            ? redirectUri
            : null;
    }

    /// <summary>
    ///     Returns the frontend page a sign-in returns to, or <see langword="null" /> when the page is not on an origin
    ///     that serves the user interface of this installation.
    /// </summary>
    /// <remarks>
    ///     The access token of the session travels in the fragment of this address, so fewer origins qualify than may
    ///     call the API: the origin of <c>MEISTER_PUBLIC_BASE_URL</c>, the origins in <c>CORS_ORIGINS</c>, the origin the
    ///     API was reached at when <c>MEISTER_PUBLIC_BASE_URL</c> is not set, and in Development the local frontend
    ///     origins. The origins that SCM providers declare for their browser extensions do not qualify, because anyone
    ///     who publishes an extension can serve pages on some of them. The sign-in of platform administrators and the
    ///     sign-in of tenant users apply this rule when the sign-in starts and again at the callback.
    /// </remarks>
    public static Uri? TryResolveUserInterfaceReturnUrl(
        string? returnUrl,
        HttpRequest request,
        IConfiguration configuration,
        bool isDevelopment)
    {
        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var returnUri) || !IsSupportedRedirectScheme(returnUri))
        {
            return null;
        }

        var origin = returnUri.GetLeftPart(UriPartial.Authority);
        return UserInterfaceOrigins(request, configuration, isDevelopment).Contains(origin, StringComparer.OrdinalIgnoreCase)
            ? returnUri
            : null;
    }

    /// <summary>
    ///     Returns <paramref name="returnUri" />, checked before, with <paramref name="fragmentValues" /> in its fragment.
    ///     The values travel in the fragment because a browser sends no fragment to a server or in a referrer.
    /// </summary>
    public static Uri WithFragment(Uri returnUri, IReadOnlyDictionary<string, string?> fragmentValues)
    {
        var fragment = string.Join(
            "&",
            fragmentValues
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
                .Select(entry => $"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value!)}"));

        var builder = new UriBuilder(returnUri)
        {
            Fragment = fragment,
        };

        return builder.Uri;
    }

    /// <summary>Answers with <c>302 Found</c> to an address one of the methods above has checked.</summary>
    public static IActionResult RedirectTo(HttpResponse response, Uri redirectUri)
    {
        response.StatusCode = StatusCodes.Status302Found;
        response.Headers.Location = redirectUri.AbsoluteUri;
        return new EmptyResult();
    }

    private static IEnumerable<string> UserInterfaceOrigins(
        HttpRequest request,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var publicUiOrigin = PublicApplicationUrlResolver.GetConfiguredPublicUiOrigin(configuration);
        if (publicUiOrigin is not null)
        {
            yield return publicUiOrigin;
        }
        else if (PublicApplicationUrlResolver.GetConfiguredPublicBaseUrl(configuration) is null)
        {
            // Without a configured address, the frontend is expected on the origin the browser reached the API at. The
            // browser that follows a link sets the host of that request, so a link cannot name another origin here. The
            // origin is built through Uri like the one of the return URL, so an explicit default port compares equal.
            yield return new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? -1)
                .Uri.GetLeftPart(UriPartial.Authority);
        }

        foreach (var origin in BrowserOriginPolicy.GetConfiguredOrigins(configuration))
        {
            yield return origin;
        }

        if (isDevelopment)
        {
            foreach (var origin in BrowserOriginPolicy.LocalDevelopmentOrigins)
            {
                yield return origin;
            }
        }
    }

    private static bool IsSupportedRedirectScheme(Uri redirectUri)
    {
        return string.Equals(redirectUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(redirectUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
