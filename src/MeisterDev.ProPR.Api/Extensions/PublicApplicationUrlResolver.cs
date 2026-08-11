// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Api.Extensions;

internal static class PublicApplicationUrlResolver
{
    public static Uri GetApplicationBaseUri(HttpRequest request, IConfiguration configuration)
    {
        if (TryGetConfiguredPublicBaseUri(configuration, out var configuredBaseUri))
        {
            return configuredBaseUri;
        }

        var builder = new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? -1)
        {
            Path = request.PathBase.HasValue ? $"{request.PathBase.Value!.TrimEnd('/')}/" : "/",
        };

        return builder.Uri;
    }

    public static string? GetConfiguredPublicBaseUrl(IConfiguration configuration)
    {
        return TryGetConfiguredPublicBaseUri(configuration, out var configuredBaseUri)
            ? configuredBaseUri.AbsoluteUri.TrimEnd('/')
            : null;
    }

    /// <summary>
    ///     Returns the scheme, host and port the user interface is served from, or <see langword="null" />
    ///     when no public base URL is configured.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The configured value names the API and may carry a path prefix such as <c>/api</c>, so only its
    ///         authority is taken: the frontend calls the API at <c>/api</c> on its own origin, which puts both
    ///         on one host with the interface at the root.
    ///     </para>
    ///     <para>
    ///         Only <c>http</c> and <c>https</c> qualify. A path-only value such as <c>/api</c> parses as an
    ///         absolute <c>file://</c> URI on Linux, whose authority is empty, and an origin built from that
    ///         would go into the browser allow-list and into links ProPR sends to a pull request.
    ///     </para>
    ///     <para>
    ///         A value carrying credentials yields nothing. <see cref="Uri.GetLeftPart" /> keeps user info in the
    ///         authority, so a configured <c>https://user:secret@host</c> would put the secret in front of a
    ///         browser and inside links posted on pull requests, where it cannot be withdrawn. An origin with
    ///         credentials also never matches a browser's <c>Origin</c> header, so it has no use to keep.
    ///     </para>
    /// </remarks>
    public static string? GetConfiguredPublicUiOrigin(IConfiguration configuration)
    {
        if (!TryGetConfiguredPublicBaseUri(configuration, out var configuredBaseUri))
        {
            return null;
        }

        var isBrowserScheme =
            string.Equals(configuredBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || string.Equals(configuredBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

        if (!isBrowserScheme || !string.IsNullOrEmpty(configuredBaseUri.UserInfo))
        {
            return null;
        }

        return configuredBaseUri.GetLeftPart(UriPartial.Authority);
    }

    private static bool TryGetConfiguredPublicBaseUri(IConfiguration configuration, out Uri publicBaseUri)
    {
        if (Uri.TryCreate(configuration["MEISTER_PUBLIC_BASE_URL"], UriKind.Absolute, out var configuredUri))
        {
            publicBaseUri = EnsureTrailingSlash(configuredUri);
            return true;
        }

        publicBaseUri = null!;
        return false;
    }

    private static Uri EnsureTrailingSlash(Uri uri)
    {
        if (uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        var builder = new UriBuilder(uri)
        {
            Path = string.IsNullOrWhiteSpace(uri.AbsolutePath)
                ? "/"
                : $"{uri.AbsolutePath.TrimEnd('/')}/",
        };

        return builder.Uri;
    }
}
