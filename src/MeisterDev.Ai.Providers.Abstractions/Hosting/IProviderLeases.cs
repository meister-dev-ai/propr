// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>A held lease on one named resource. Releasing it is disposing it.</summary>
public interface IProviderNamedLease : IAsyncDisposable
{
    /// <summary>The resource this lease holds.</summary>
    string ResourceName { get; }
}

/// <summary>
///     The outcome of asking for a named lease: either the lease, or the connection that already holds it.
/// </summary>
/// <param name="Lease">The held lease, or null when another connection holds the resource.</param>
/// <param name="HeldByConnection">
///     The display name of the connection holding the resource, when the lease was not granted. Named so the
///     action can say which connection to stop rather than reporting that something is in use.
/// </param>
public sealed record ProviderLeaseOutcome(IProviderNamedLease? Lease, string? HeldByConnection)
{
    /// <summary>Whether the caller holds the resource.</summary>
    public bool Acquired => this.Lease is not null;
}

/// <summary>
///     Arbitrates one named resource across the connections of one family, across the whole installation.
/// </summary>
/// <remarks>
///     The resource this exists for is a listener port. A family binds the port in its own network namespace, and
///     two connections of the same family cannot hold it at once, so the second is refused with the first named
///     rather than failing on the bind with an error that names nothing.
/// </remarks>
public interface IProviderLeases
{
    /// <summary>Waits up to <paramref name="timeout" /> for the named resource.</summary>
    /// <remarks>
    ///     The timeout governs how long the caller keeps trying, and one attempt is always made: a timeout of
    ///     nothing asks the resource once and reports who holds it. Each attempt is bounded by the host, so a
    ///     stalled database ends the call with a timeout rather than holding the caller.
    /// </remarks>
    /// <param name="resourceName">The resource, named by the family; unique within it.</param>
    /// <param name="timeout">How long to wait before reporting that another connection holds it.</param>
    /// <param name="ct">Cancels the wait.</param>
    /// <exception cref="ProviderHostTimeoutException">One attempt ran past the host's own bound.</exception>
    Task<ProviderLeaseOutcome> AcquireAsync(string resourceName, TimeSpan timeout, CancellationToken ct = default);
}
