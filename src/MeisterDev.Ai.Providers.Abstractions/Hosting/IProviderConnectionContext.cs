// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Everything a provider family may do against one connection, and nothing else.
/// </summary>
/// <remarks>
///     <para>
///         The set is closed. Left open, each family would build its own HTTP client, its own database access,
///         its own locking and its own state table, and the concurrency semantics alone are not something to
///         solve once per family.
///     </para>
///     <para>
///         The stateful handles are bound to one connection. Credentials, leases, the keyed store and the health
///         signal all act on the connection the host handed the family, and none of them takes a connection
///         identifier the family could pass a different value for. The family never sees the stored credential
///         envelope or any protection type either: it deals in named values and the host does the encoding.
///     </para>
///     <para>
///         <see cref="Http" /> is the exception and is bound to a purpose, not to the connection's endpoint. A
///         family reaches more than one host on one connection: a token service, a discovery surface and the
///         model endpoint are three different addresses for several families. What bounds it is the
///         installation's egress policy, which the host composes innermost in every client it hands out and
///         which a family cannot opt out of. It is not a per-connection boundary and this interface does not
///         claim one.
///     </para>
/// </remarks>
public interface IProviderConnectionContext
{
    /// <summary>The only way this family reaches the network.</summary>
    IProviderHttpClientFactory Http { get; }

    /// <summary>Exclusive access to this connection's stored credential.</summary>
    IProviderCredentialSessions Credentials { get; }

    /// <summary>Arbitration of a named resource across the connections of this family.</summary>
    IProviderLeases Leases { get; }

    /// <summary>Short-lived named values this family keeps between the start of a flow and its completion.</summary>
    IProviderKeyedStore Store { get; }

    /// <summary>Reporting what this family learned about the connection's credential.</summary>
    IProviderHealthSignal Health { get; }
}

/// <summary>
///     What a family may do while one action invocation is in flight: everything it may do against the
///     connection, plus the two things that belong to the invocation itself.
/// </summary>
public interface IProviderActionContext : IProviderConnectionContext
{
    /// <summary>
    ///     Tripped when the invocation's window closes and when the process is shutting down. Cooperative: a
    ///     family that observes it returns at the window, and one that blocks a thread without observing it is
    ///     not cut — the invocation is expired either way, so neither the dispatch call nor the operator's view
    ///     is held.
    /// </summary>
    CancellationToken Cancellation { get; }

    /// <summary>Writes the terminal state of this invocation when the family's own work finishes.</summary>
    IProviderInvocationReporter Invocation { get; }
}
