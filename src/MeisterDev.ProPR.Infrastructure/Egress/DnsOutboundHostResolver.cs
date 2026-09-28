// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;

namespace MeisterDev.ProPR.Infrastructure.Egress;

/// <summary>Resolves a host through the name service this host uses for every other outbound connection.</summary>
internal sealed class DnsOutboundHostResolver : IOutboundHostResolver
{
    /// <inheritdoc />
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        return Dns.GetHostAddressesAsync(host, ct);
    }
}
