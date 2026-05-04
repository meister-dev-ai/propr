// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Extensions;

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
