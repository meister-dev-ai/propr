// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>Canonical issuer and exact browser callbacks for the ProPR Lens public client.</summary>
public sealed record ExtensionAuthorizationOptions(string? Issuer, IReadOnlyList<string> RedirectUris, string? UnavailableReason = null)
{
    public const string ClientId = "propr-lens";
    public bool Enabled => this.UnavailableReason is null && this.Issuer is not null && this.RedirectUris.Count > 0;

    public static ExtensionAuthorizationOptions FromConfiguration(IConfiguration configuration)
    {
        var issuer = configuration["MEISTER_EXTENSION_AUTH_ISSUER"]?.Trim().TrimEnd('/');
        var raw = configuration["MEISTER_EXTENSION_AUTH_REDIRECT_URIS"];
        const string invalidRedirects = "MEISTER_EXTENSION_AUTH_REDIRECT_URIS must be a JSON array of callback strings.";
        string[] redirects;
        try
        {
            redirects = string.IsNullOrWhiteSpace(raw)
                ? []
                : JsonSerializer.Deserialize<string[]>(raw) ?? throw new InvalidOperationException(invalidRedirects);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(invalidRedirects);
        }

        if (string.IsNullOrWhiteSpace(issuer) && redirects.Length == 0)
        {
            return new(null, []);
        }

        if (!ValidHttps(issuer) || redirects.Length == 0 || redirects.Any(value => !ValidCallback(value)))
        {
            throw new InvalidOperationException(
                "Extension sign-in requires a canonical HTTPS issuer and exact HTTPS callbacks without credentials, queries or fragments.");
        }

        var publicUi = configuration["MEISTER_PUBLIC_BASE_URL"]?.Trim();
        return new(
            new Uri(issuer!).AbsoluteUri.TrimEnd('/'), redirects.Distinct(StringComparer.Ordinal).ToArray(),
            !string.IsNullOrEmpty(publicUi) && !ValidHttps(publicUi) ? "insecure_ui_origin" : null);
    }

    private static bool ValidHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                                                     && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
                                                     uri.Fragment.Length == 0
                                                     && !value!.Contains('*', StringComparison.Ordinal);

    private static bool ValidCallback(string? value) => ValidHttps(value) && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                                                                          && uri.IsDefaultPort && Regex.IsMatch(
                                                                              uri.Host,
                                                                              "^(?:[a-p]{32}\\.chromiumapp\\.org|[0-9a-f]{40}\\.extensions\\.allizom\\.org)$",
                                                                              RegexOptions.CultureInvariant);
}
