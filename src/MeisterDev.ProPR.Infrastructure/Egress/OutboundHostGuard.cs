// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using MeisterDev.Ai.Providers.Egress;

namespace MeisterDev.ProPR.Infrastructure.Egress;

/// <summary>
///     Applies the installation's egress posture to an address that is reached by something other than this
///     host's HTTP clients, such as the git binary fetching a repository mirror.
/// </summary>
/// <remarks>
///     The connect-time guard on an HTTP handler covers what the handler dials. A subprocess opens its own
///     sockets, so the address it is about to be handed is resolved and classified here first, with the ranges
///     the handler applies. The check runs before the subprocess starts, so the answer is a reason a caller can
///     record instead of a connection failure to interpret.
///     <para>
///         A subprocess resolves the host name again when it connects, which is a second answer the name
///         server can give differently. The check therefore reports the addresses it classified, so a caller
///         that can tell the subprocess which addresses to use connects to what was checked.
///     </para>
///     <para>
///         Only http, https and a path on this machine reach the check as a permitted destination. A remote
///         on another transport is refused, because every remote ProPR builds comes from an http or https
///         host address, and the scheme rule and the address pin are both written for those two schemes.
///     </para>
/// </remarks>
/// <param name="egressUrlPolicy">What this installation permits an operator-entered address to reach.</param>
/// <param name="resolver">Resolves a host name to the addresses a connection to it would reach.</param>
internal sealed class OutboundHostGuard(EgressUrlPolicy egressUrlPolicy, IOutboundHostResolver resolver)
{
    /// <summary>
    ///     Whether this installation permits <paramref name="address" />, and the addresses it was permitted
    ///     at. An address with no network host, such as a local path, is permitted: nothing leaves the machine.
    /// </summary>
    /// <param name="address">The address the caller is about to hand to a subprocess.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<OutboundHostDecision> CheckAsync(string? address, CancellationToken ct)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return OutboundHostDecision.Permitted;
        }

        // A file URI naming no host, or naming localhost, is a path on this machine: nothing leaves it, and
        // there is no transport to hold to a rule.
        if (uri.IsFile && (string.IsNullOrEmpty(uri.Host) || uri.IsLoopback))
        {
            return OutboundHostDecision.Permitted;
        }

        // Every remote ProPR hands to git is built from the http or https host address an operator entered,
        // so an address on another transport is one nothing here produces. It is refused instead of
        // classified by address: the repository rule below decides a scheme this installation dials, and the
        // pin a caller applies afterwards holds an http or https connection only, so an ssh remote would pass
        // through both and still reach whatever its name answers with when git connects.
        if (!IsWebTransport(uri))
        {
            return OutboundHostDecision.Refused(
                $"The repository's address uses the '{uri.Scheme}' scheme. A repository is reached over https, "
                + "or over http on a private host where the installation permits one, and no other transport is dialled.");
        }

        // The rule an operator-entered source-control address is held to decides the remote as well: the
        // scheme, and a host that is already a literal address, before anything is resolved. It applies under
        // every posture, because plain http to a public host puts the credential of every git request on the
        // wire in the clear whatever the installation permits privately.
        if (egressUrlPolicy.GetRepositoryHostRefusalReason(address, "The repository's address") is { } refusal)
        {
            return OutboundHostDecision.Refused(refusal);
        }

        if (egressUrlPolicy.AllowPrivateEgress)
        {
            return OutboundHostDecision.Permitted;
        }

        if (EgressAddressPolicy.IsBlockedEgressHost(uri.Host))
        {
            return OutboundHostDecision.Refused(Refusal(uri.Host));
        }

        // A literal address is the address the subprocess connects to. There is no name for a resolver to
        // answer a second time, so the classification above is the whole answer and nothing is resolved: a
        // resolver that refused or permitted something else must not decide a literal destination.
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            return OutboundHostDecision.Permitted;
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(uri.Host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return OutboundHostDecision.Refused($"Host '{uri.Host}' could not be resolved, so the address it would be reached at is unknown.");
        }

        if (addresses.Length == 0)
        {
            return OutboundHostDecision.Refused($"Host '{uri.Host}' resolves to no address, so there is nothing to connect to.");
        }

        // A name that resolves to a mix of public and internal addresses, or is being rebound, must not be
        // reachable either, so any blocked address in the answer refuses the whole host.
        return Array.Exists(addresses, EgressAddressPolicy.IsBlockedEgressAddress)
            ? OutboundHostDecision.Refused(Refusal(uri.Host))
            : new OutboundHostDecision(null, addresses);
    }

    /// <summary>Whether <paramref name="uri" /> is reached over http or https.</summary>
    /// <param name="uri">The address handed to the subprocess.</param>
    /// <remarks>
    ///     These are the two transports this host dials. The source-control address rule is written for them:
    ///     it requires https of a public host and permits http only where the address is private and the
    ///     installation permits private egress.
    /// </remarks>
    private static bool IsWebTransport(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static string Refusal(string host)
    {
        return $"Host '{host}' resolves to a private, loopback or link-local address. "
               + $"Set {EgressUrlPolicy.PrivateEgressOptIn} on the installation to permit one.";
    }
}

/// <summary>What the guard decided about one address.</summary>
/// <param name="RefusalReason">
///     Why this installation refuses the address, or <see langword="null" /> when it permits it.
/// </param>
/// <param name="ApprovedAddresses">
///     The addresses the host name resolves to and this check classified as permitted. Empty where a caller
///     has nothing to pin: the installation permits private egress, the address carries no host name, or the
///     host is a literal address the subprocess does not resolve either.
/// </param>
internal sealed record OutboundHostDecision(string? RefusalReason, IReadOnlyList<IPAddress> ApprovedAddresses)
{
    /// <summary>A permitted address with nothing for the caller to pin.</summary>
    public static OutboundHostDecision Permitted { get; } = new(null, []);

    /// <summary>A refusal carrying <paramref name="reason" />.</summary>
    /// <param name="reason">Why the installation refuses the address.</param>
    public static OutboundHostDecision Refused(string reason)
    {
        return new OutboundHostDecision(reason, []);
    }
}
