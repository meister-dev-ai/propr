// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using MeisterProPR.Application.Networking;

namespace MeisterProPR.Infrastructure.Net;

/// <summary>
///     Builds a <see cref="SocketsHttpHandler" /> that refuses to connect to blocked egress addresses.
///     The destination IP is validated at connect time (defeating DNS-rebinding, since the check runs on the
///     address the socket actually connects to), and automatic redirects are disabled so a 3xx response
///     cannot bounce the request to an internal target.
/// </summary>
public static class GuardedEgressHttpHandler
{
    /// <summary>Creates the guarded handler.</summary>
    /// <param name="allowPrivateEgress">
    ///     When <c>true</c> (Development) the egress check is skipped so local providers — e.g. a localhost
    ///     LiteLLM — remain reachable. Redirects stay disabled regardless.
    /// </param>
    public static SocketsHttpHandler Create(bool allowPrivateEgress)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
        };

        if (!allowPrivateEgress)
        {
            handler.ConnectCallback = ConnectGuardedAsync;
        }

        return handler;
    }

    private static async ValueTask<Stream> ConnectGuardedAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        // Reject if the host resolves to nothing or to ANY blocked address: a name that resolves to a mix of
        // public and internal addresses (or is being rebound) must not be reachable.
        if (addresses.Length == 0 || Array.Exists(addresses, EgressAddressPolicy.IsBlockedEgressAddress))
        {
            throw new HttpRequestException($"Refused to connect to host '{host}': it resolves to a blocked egress address.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect to the exact set of addresses that was just validated (no re-resolution) to close the
            // check-then-connect race.
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
