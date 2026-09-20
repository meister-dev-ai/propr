// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Provider-neutral description of a probe/verify target, passed to <c>IAiProviderDriver.ValidateProbeTarget</c>
///     so each provider driver can enforce its own base-URL, egress, and auth-shape rules without the controller
///     branching on provider kind.
/// </summary>
/// <param name="BaseUrl">The connection base URL to probe.</param>
/// <param name="AuthMode">The configured authentication mode.</param>
/// <param name="HasApiKey">Whether a non-empty API key was supplied.</param>
public sealed record AiProbeTarget(string BaseUrl, string AuthMode, bool HasApiKey)
{
    /// <summary>
    ///     Whether this installation permits an address on a private, loopback or link-local range, which reaches
    ///     a self-hosted or on-premise endpoint.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Supplied by the host from the installation's own egress settings, so a family applies the rule the
    ///         installation stated rather than a rule of its own. A family compiled into the host reads the
    ///         setting where it is composed; a family loaded from a directory is constructed with no arguments and
    ///         has no other way to learn it, and one that assumed the strictest posture would refuse the
    ///         self-hosted gateway the opt-in exists to reach.
    ///     </para>
    ///     <para>
    ///         It says what the installation permits, not what the family must accept: the host applies the same
    ///         rule to the address before the family is asked, and the connect-time address check on every client
    ///         the host hands out is separate again and cannot be opted out of. A family may still refuse more.
    ///     </para>
    ///     <para>
    ///         Off by default, so a caller that states nothing presents the strictest posture. The shared driver
    ///         checks state nothing, and that makes a family's answer to them the same on every installation.
    ///     </para>
    /// </remarks>
    public bool AllowsPrivateAddress { get; init; }

    /// <summary>Whether this installation permits a plain-http address, which is a Development-only relaxation.</summary>
    /// <remarks>
    ///     Separate from <see cref="AllowsPrivateAddress" />, because reaching a self-hosted endpoint does not
    ///     relax transport security: an on-premise endpoint is still reached over https.
    /// </remarks>
    public bool AllowsInsecureScheme { get; init; }
}
