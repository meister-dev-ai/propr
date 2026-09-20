// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>What a family is reaching the network for, which decides the pipeline the host composes.</summary>
public enum ProviderHttpPurpose
{
    /// <summary>Checking whether a configured endpoint is reachable and the credential works.</summary>
    Probe = 0,

    /// <summary>Configuration-time work: discovering models, exchanging or renewing a credential.</summary>
    Admin = 1,

    /// <summary>Model calls a review makes.</summary>
    Runtime = 2,
}

/// <summary>
///     The only way a family reaches the network. A family never constructs an <see cref="HttpClient" /> or a
///     socket for provider traffic.
/// </summary>
/// <remarks>
///     <para>
///         The host composes a pooled handler with its own connect-time address check innermost, then the
///         handlers the purpose needs, then the family's. That check defends against an address an operator
///         typed, not against family code, and it is the control the published security documentation states, so
///         it stays host-owned and cannot be opted out of.
///     </para>
///     <para>
///         A factory rather than a handler, so client pooling and connection rotation stay with the host: a
///         family holding a handler it built itself would pin stale name resolution and leak sockets.
///     </para>
/// </remarks>
public interface IProviderHttpClientFactory
{
    /// <summary>Builds a client for one purpose, wrapping the family's own handlers outside the host's.</summary>
    /// <param name="purpose">What the calls on this client are for.</param>
    /// <param name="outerHandlers">
    ///     Handlers of the family's own, applied outside the host's. A family that signs or retries its own
    ///     requests contributes them here rather than building a client.
    /// </param>
    HttpClient Create(ProviderHttpPurpose purpose, IReadOnlyList<DelegatingHandler>? outerHandlers = null);
}
