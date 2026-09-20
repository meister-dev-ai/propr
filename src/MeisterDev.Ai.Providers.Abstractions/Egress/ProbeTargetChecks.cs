// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     The checks every provider's probe validation is built from, and nothing about any particular provider.
/// </summary>
/// <remarks>
///     <para>
///         What belongs here is the mechanics each driver would otherwise reimplement: parsing the URL, applying
///         the egress policy, and requiring a key. Which hosts are acceptable, which authentication modes a
///         provider reads, and what its endpoint has to name are the driver's own rules and live with the driver -
///         a provider added later must not need this file edited to describe itself.
///     </para>
///     <para>
///         It sits in the contract assembly because a family supplied by an add-in has to apply the same rules as
///         one compiled in, and it reaches nothing else from here. A family that had to restate the address
///         classification would carry a second copy of a security control, and the two would drift.
///     </para>
/// </remarks>
public static class ProbeTargetChecks
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
    /// <param name="subject">
    ///     What the refusal names. The address a connection is reached at is <c>baseUrl</c>; a value an operator
    ///     entered into a field a provider family declared is named by that field's label, so the refusal points
    ///     at the input the operator has to fix.
    /// </param>
    /// <returns>An error to report, or <see langword="null" /> when the target is permitted.</returns>
    public static string? Egress(Uri uri, bool allowPrivateEgress, bool allowInsecureScheme, string subject = "baseUrl")
    {
        ArgumentNullException.ThrowIfNull(uri);

        // An address carrying userinfo holds a credential in a field nothing treats as one: it is stored in the
        // clear beside the profile rather than in the protected credential store, and it is not what any provider
        // family reads its credential from. Where a provider expects a key on the address, the host carries it as
        // a default query parameter, whose value is held and rendered as credential material.
        if (CarriesUserInfo(uri))
        {
            return $"{subject} must not carry a credential in the address. Put it in the credential fields, or in "
                   + "a default query parameter where the provider expects one there.";
        }

        // https is required unless a Development-local provider needs plain http. The private-egress opt-in
        // intentionally does NOT relax the scheme - a self-hosted or on-premise endpoint must still use https.
        var isHttps = string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        // The flag admits http and nothing else. Testing only for "not https" admitted ftp, file and any custom
        // scheme a family invented, none of which this host knows how to reach.
        if (!isHttps && !(allowInsecureScheme && isHttp))
        {
            return allowInsecureScheme
                ? $"{subject} must use https or http."
                : $"{subject} must use https.";
        }

        // The private/loopback/link-local block is lifted only when private egress is permitted - Development, or
        // the operator opt-in - so an on-premise endpoint can be configured. It stays blocked by default.
        if (!allowPrivateEgress && EgressAddressPolicy.IsBlockedEgressHost(uri.Host))
        {
            return $"{subject} must not target a private, loopback, or link-local address.";
        }

        return null;
    }

    /// <summary>Applies the transport-security and egress policy stated on the target being validated.</summary>
    /// <param name="uri">The parsed base URL.</param>
    /// <param name="target">The target, which carries what this installation permits an address to reach.</param>
    /// <returns>An error to report, or <see langword="null" /> when the target is permitted.</returns>
    /// <remarks>
    ///     What a family supplied by an add-in calls: it is constructed with no arguments, so the installation's
    ///     settings reach it on the target rather than through its constructor.
    /// </remarks>
    public static string? Egress(Uri uri, AiProbeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return Egress(uri, target.AllowsPrivateAddress, target.AllowsInsecureScheme);
    }

    /// <summary>Whether the address holds a user name or a password in its userinfo component.</summary>
    /// <param name="uri">The parsed address.</param>
    /// <remarks>
    ///     Shared so the browser-open policy asks the same question the egress policy does, in its own words. An
    ///     address holding userinfo also renders that userinfo into any log line quoting it, which the safe
    ///     renderers drop, but refusing it is what keeps it out of storage in the first place.
    /// </remarks>
    public static bool CarriesUserInfo(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return !string.IsNullOrEmpty(uri.UserInfo);
    }

    /// <summary>
    ///     Requires a key stored under one of the authentication modes a key can be read from.
    /// </summary>
    /// <param name="target">The probe target.</param>
    /// <param name="authModes">
    ///     The authentication modes the caller reads a key from, as the family declared them. A target holding any
    ///     other mode is refused whether or not a key was supplied.
    /// </param>
    /// <param name="message">A provider-specific refusal, or <see langword="null" /> for the generic one.</param>
    /// <returns>An error to report, or <see langword="null" /> when a key is present.</returns>
    /// <remarks>
    ///     The modes come from the caller because an authentication mode belongs to the family that declared it.
    ///     The host cannot name which of a family's modes carries a bearer key.
    /// </remarks>
    public static string? RequireApiKey(
        AiProbeTarget target,
        IReadOnlyList<string> authModes,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(authModes);

        if (!ProviderVocabulary.Names(authModes, target.AuthMode) || !target.HasApiKey)
        {
            return message ?? "An API key is required for this provider and auth mode.";
        }

        return null;
    }
}
