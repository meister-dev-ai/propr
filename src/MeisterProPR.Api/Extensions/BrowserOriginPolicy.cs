// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Extensions;

internal static class BrowserOriginPolicy
{
    private static readonly string[] FixedOrigins =
    [
        "http://localhost:3000",
        "https://localhost:3000",
        "http://localhost:5173",
        "https://localhost:5173",
        "https://dev.azure.com",
    ];

    public static string[] GetAllowedOrigins(IConfiguration configuration)
    {
        var extraOrigins = (configuration["CORS_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var publicUiOrigin = TryGetPublicUiOrigin(configuration);

        return FixedOrigins
            .Concat(extraOrigins)
            .Concat(publicUiOrigin is null ? [] : [publicUiOrigin])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsAllowedOrigin(string origin, IConfiguration configuration)
    {
        return IsAllowedOrigin(origin, GetAllowedOrigins(configuration));
    }

    public static bool IsAllowedOrigin(string origin, IReadOnlyCollection<string> allowedOrigins)
    {
        return allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
               || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && (uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
                       || uri.Host.EndsWith(".gallerycdn.vsassets.io", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? TryGetPublicUiOrigin(IConfiguration configuration)
    {
        var configuredPublicBaseUrl = PublicApplicationUrlResolver.GetConfiguredPublicBaseUrl(configuration);
        if (!Uri.TryCreate(configuredPublicBaseUrl, UriKind.Absolute, out var publicBaseUri))
        {
            return null;
        }

        return publicBaseUri.GetLeftPart(UriPartial.Authority);
    }
}
