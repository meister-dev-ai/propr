// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;

namespace MeisterDev.ProPR.Infrastructure.Egress;

/// <summary>Resolves a host name to the addresses an outbound connection to it would reach.</summary>
/// <remarks>
///     A seam, so a check over a host name can be exercised without a name server answering for it.
/// </remarks>
internal interface IOutboundHostResolver
{
    /// <summary>Returns the addresses <paramref name="host" /> resolves to.</summary>
    /// <param name="host">The host name or literal address.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}
