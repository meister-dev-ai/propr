// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Lists provider scopes and repositories through a provider-neutral contract.</summary>
public interface IRepositoryDiscoveryProvider
{
    /// <summary>Lists repositories only within the saved scope and project using the selected connection.</summary>
    Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(
        ConnectionDiscoveryContext context, string savedScope, string savedProject, CancellationToken ct = default);

    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

    /// <summary>Describes native hierarchy, source kinds and supported operations.</summary>
    ConnectionDiscoveryDescriptor Descriptor { get; }

    /// <summary>Maps selected native hierarchy values to persistent coordinates, including empty source lists.</summary>
    ConnectionDiscoveryCoordinates GetConfigurationCoordinates(ConnectionDiscoveryContext context, ConnectionDiscoveryScope scope, string? projectId);

    /// <summary>Lists scopes using only the selected operational connection.</summary>
    Task<IReadOnlyList<ConnectionDiscoveryScope>> ListScopesAsync(ConnectionDiscoveryContext context, CancellationToken ct = default);

    /// <summary>
    ///     Checks the stored credentials of the selected connection against the provider, also while the connection is
    ///     inactive, so a connection can be verified before it is activated.
    /// </summary>
    Task VerifyConnectionAsync(ConnectionDiscoveryContext context, CancellationToken ct = default);

    /// <summary>Lists native sources and their persistent coordinates using the selected connection.</summary>
    Task<IReadOnlyList<ConnectionDiscoverySource>> ListSourcesAsync(
        ConnectionDiscoveryContext context, string scopeKey, string? projectId, ProCursorSourceKind sourceKind,
        CancellationToken ct = default);

    /// <summary>Lists the provider-scoped administrative boundaries available to the client.</summary>
    Task<IReadOnlyList<string>> ListScopesAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default);

    /// <summary>Lists repositories that belong to the selected provider scope.</summary>
    Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(
        Guid clientId,
        ProviderHostRef host,
        string scopePath,
        CancellationToken ct = default);
}
