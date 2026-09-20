// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Everything one provider family may do against one connection.
/// </summary>
/// <remarks>
///     Every handle in it was built for the same connection, and none of them takes an identifier the family
///     could pass a different value for, so a family acts on the connection the host chose and on no other.
/// </remarks>
/// <param name="Http">The only route this family has to the network.</param>
/// <param name="Credentials">The connection's stored credential, to read or to take for a renewal.</param>
/// <param name="Leases">Arbitration of a named resource across this family's connections.</param>
/// <param name="Store">Short-lived named values this family keeps between the start of a flow and its end.</param>
/// <param name="Health">Reporting what this family learned about the connection's credential.</param>
public sealed record ProviderConnectionContext(
    IProviderHttpClientFactory Http,
    IProviderCredentialSessions Credentials,
    IProviderLeases Leases,
    IProviderKeyedStore Store,
    IProviderHealthSignal Health) : IProviderConnectionContext;

/// <summary>
///     Everything one provider family may do while one of its action invocations is in flight.
/// </summary>
/// <param name="Connection">What the family may do against the connection, unchanged.</param>
/// <param name="Cancellation">Tripped at the invocation's window and at process shutdown.</param>
/// <param name="Invocation">Where the family writes this invocation's terminal state.</param>
public sealed record ProviderActionContext(
    IProviderConnectionContext Connection,
    CancellationToken Cancellation,
    IProviderInvocationReporter Invocation) : IProviderActionContext
{
    /// <inheritdoc />
    public IProviderHttpClientFactory Http => this.Connection.Http;

    /// <inheritdoc />
    public IProviderCredentialSessions Credentials => this.Connection.Credentials;

    /// <inheritdoc />
    public IProviderLeases Leases => this.Connection.Leases;

    /// <inheritdoc />
    public IProviderKeyedStore Store => this.Connection.Store;

    /// <inheritdoc />
    public IProviderHealthSignal Health => this.Connection.Health;
}
