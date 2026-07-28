// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     The checks every provider's probe validation is built from, and nothing about any particular provider.
/// </summary>
/// <remarks>
///     What belongs here is the mechanics each driver would otherwise reimplement: parsing the URL, applying the
///     egress policy, and requiring a key. Which hosts are acceptable, which authentication modes a provider
///     reads, and what its endpoint has to name are the driver's own rules and live with the driver - a provider
///     added later must not need this file edited to describe itself.
/// </remarks>
internal static class ProbeTargetChecks
{
    /// <summary>Parses the target's base URL.</summary>
    /// <param name="target">The probe target.</param>
    /// <param name="uri">The parsed URL, when it parsed.</param>
    /// <returns>An error to report, or <see langword="null" /> when the URL is usable.</returns>
    public static string? AbsoluteUrl(AiProbeTarget target, out Uri uri)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target.BaseUrl, UriKind.Absolute, out var parsed))
        {
            uri = null!;
            return "baseUrl must be an absolute URL.";
        }

        uri = parsed;
        return null;
    }

    /// <summary>Applies the transport-security and egress policy to a parsed target.</summary>
    /// <param name="uri">The parsed base URL.</param>
    /// <param name="allowPrivateEgress">
    ///     When true, a private, loopback, or link-local host is permitted so a self-hosted or on-premise endpoint
    ///     can be configured (Development, or the operator opt-in).
    /// </param>
    /// <param name="allowInsecureScheme">
    ///     When true (Development only), a plain-http baseUrl is permitted so a local provider stays reachable.
    /// </param>
    /// <returns>An error to report, or <see langword="null" /> when the target is permitted.</returns>
    public static string? Egress(Uri uri, bool allowPrivateEgress, bool allowInsecureScheme)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // https is required unless a Development-local provider needs plain http. The private-egress opt-in
        // intentionally does NOT relax the scheme - a self-hosted or on-premise endpoint must still use https.
        if (!allowInsecureScheme
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "baseUrl must use https.";
        }

        // The private/loopback/link-local block is lifted only when private egress is permitted - Development, or
        // the operator opt-in - so an on-premise endpoint can be configured. It stays blocked by default.
        if (!allowPrivateEgress && EgressAddressPolicy.IsBlockedEgressHost(uri.Host))
        {
            return "baseUrl must not target a private, loopback, or link-local address.";
        }

        return null;
    }

    /// <summary>Requires a key stored under the plain API-key authentication mode.</summary>
    /// <param name="target">The probe target.</param>
    /// <param name="message">A provider-specific refusal, or <see langword="null" /> for the generic one.</param>
    /// <returns>An error to report, or <see langword="null" /> when a key is present.</returns>
    public static string? RequireApiKey(AiProbeTarget target, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.AuthMode != AiAuthMode.ApiKey || !target.HasApiKey)
        {
            return message ?? "An API key is required for this provider and auth mode.";
        }

        return null;
    }
}
