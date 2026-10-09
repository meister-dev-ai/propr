// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Api.Extensions;

internal static class BrowserOriginPolicy
{
    private static readonly string[] FixedOrigins =
    [
        "http://localhost:3000",
        "https://localhost:3000",
        "http://localhost:5173",
        "https://localhost:5173",
    ];

    public static string[] GetAllowedOrigins(
        IConfiguration configuration,
        IEnumerable<ScmBrowserOriginDeclaration> declarations)
    {
        var extraOrigins = (configuration["CORS_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var publicUiOrigin = PublicApplicationUrlResolver.GetConfiguredPublicUiOrigin(configuration);

        return FixedOrigins
            .Concat(declarations.SelectMany(declaration => declaration.AllowedOrigins))
            .Concat(extraOrigins)
            .Concat(publicUiOrigin is null ? [] : [publicUiOrigin])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsAllowedOrigin(
        string origin, IConfiguration configuration,
        IReadOnlyCollection<ScmBrowserOriginDeclaration> declarations)
    {
        return IsAllowedOrigin(origin, GetAllowedOrigins(configuration, declarations), declarations);
    }

    public static bool IsAllowedOrigin(
        string origin, IReadOnlyCollection<string> allowedOrigins,
        IEnumerable<ScmBrowserOriginDeclaration> declarations)
    {
        return allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
               || (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                   && declarations.SelectMany(declaration => declaration.AllowedHostSuffixes)
                       .Any(suffix => uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    ///     Whether the origin belongs to a browser extension.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Matched by scheme rather than by value because an extension origin cannot be enumerated:
    ///         Firefox derives <c>moz-extension://</c> from a UUID it randomises for every installation,
    ///         so no allow-list entry could ever name it.
    ///     </para>
    ///     <para>
    ///         Chromium exempts an extension's own requests from cross-origin checks when it holds the
    ///         host permission, so this is not needed there. Firefox does not, and enforces them on the
    ///         extension's background worker as it would on any page.
    ///     </para>
    /// </remarks>
    public static bool IsExtensionOrigin(string origin)
    {
        return origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
               || origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
    }
}
