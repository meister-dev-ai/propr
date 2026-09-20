// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Egress;

/// <summary>
///     What this installation permits an operator-entered address to reach.
/// </summary>
/// <remarks>
///     <para>
///         The two settings are resolved once where the host is composed and carried as one value, so every place
///         that checks an address against them asks the same question. They are controls over operator input
///         rather than over provider code: a base URL and a declared address are values a person typed, and the
///         rules stay host-owned wherever the value is used.
///     </para>
///     <para>
///         The scheme and the address range are separate: the private-egress opt-in reaches a self-hosted
///         endpoint and does not relax https, because an on-premise endpoint still has to be reached securely.
///     </para>
/// </remarks>
/// <param name="AllowPrivateEgress">
///     Whether a private, loopback or link-local host is permitted, which reaches a self-hosted or on-premise
///     endpoint. Off by default; on in Development and through the operator's opt-in.
/// </param>
/// <param name="AllowInsecureScheme">Whether plain http is permitted, which is a Development-only relaxation.</param>
public sealed record EgressUrlPolicy(bool AllowPrivateEgress, bool AllowInsecureScheme)
{
    /// <summary>The default posture: https only, and nothing private, loopback or link-local.</summary>
    public static EgressUrlPolicy Locked { get; } = new(AllowPrivateEgress: false, AllowInsecureScheme: false);

    /// <summary>
    ///     Why this installation refuses <paramref name="value" />, or <see langword="null" /> when it permits it.
    /// </summary>
    /// <param name="value">The address as the operator entered it.</param>
    /// <param name="subject">What the refusal names, so it points at the input that has to change.</param>
    /// <param name="admitLoopbackOverHttp">
    ///     Whether a loopback host over plain http is permitted for this value. It is, for a provider family that
    ///     declared it needs the host process and the operator's browser on one machine: the address such a family
    ///     configures is a loopback callback, and without the carve-out an operator would have to open private
    ///     egress across the whole installation, for every purpose and every family, to render a form holding one.
    ///     The carve-out reaches loopback alone — any other private address still needs the installation opt-in.
    /// </param>
    public string? GetRefusalReason(string? value, string subject, bool admitLoopbackOverHttp = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return $"{subject} must be an absolute URL.";
        }

        // Checked before the carve-out. The carve-out is about the scheme and the address, and userinfo is
        // neither: a credential in the address is stored in the clear beside the profile whatever host it names,
        // so http://user:secret@127.0.0.1 has to be refused for the same reason every other address is.
        if (ProbeTargetChecks.CarriesUserInfo(uri))
        {
            return ProbeTargetChecks.Egress(uri, this.AllowPrivateEgress, this.AllowInsecureScheme, subject);
        }

        return admitLoopbackOverHttp && IsLoopbackOverHttp(uri)
            ? null
            : ProbeTargetChecks.Egress(uri, this.AllowPrivateEgress, this.AllowInsecureScheme, subject);
    }

    // Plain http alone. The carve-out exists because a loopback callback cannot present a certificate, and that
    // reason does not extend to any other scheme: admitting https here would take an https loopback address past
    // the private-egress check as well, and admitting anything else would take an arbitrary scheme past both.
    private static bool IsLoopbackOverHttp(Uri uri)
    {
        return uri.IsLoopback && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
    }
}
