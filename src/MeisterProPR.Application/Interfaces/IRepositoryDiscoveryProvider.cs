// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Lists provider scopes and repositories through a provider-neutral contract.</summary>
public interface IRepositoryDiscoveryProvider
{
    /// <summary>The provider family implemented by this adapter.</summary>
    ScmProvider Provider { get; }

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
