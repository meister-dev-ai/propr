// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Builds the set of host primitives one provider family may use against one stored connection.
/// </summary>
/// <remarks>
///     <para>
///         This is the route by which a provider family reaches the host. The primitives are bound to one
///         connection and a family never names one, so the host builds the set and hands it over at the point it
///         asks the family to act on that connection.
///     </para>
///     <para>
///         What comes back holds nothing from the work that asked for it, so a family may keep it for as long as
///         it keeps whatever the host built for that connection. A review builds one client and calls it for as
///         long as the review runs, which is longer than the work that built it.
///     </para>
/// </remarks>
public interface IProviderConnectionContextFactory
{
    /// <summary>
    ///     Builds the set for one stored connection, or returns null for a connection that is not stored and for
    ///     one whose family this build cannot resolve.
    /// </summary>
    /// <remarks>
    ///     A configuration-time probe answers for values an operator typed rather than for a row, so there is no
    ///     connection for these primitives to be bound to. Such a probe takes a
    ///     <see cref="ProviderProbeContext" /> over the host's client factory instead, which is the one thing it
    ///     does need: a family discovers models and verifies an endpoint by calling it.
    /// </remarks>
    /// <param name="connection">The stored connection.</param>
    IProviderConnectionContext? ForConnection(AiConnectionDto connection);
}
