// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Reads the model catalog as it applies to one client, resolving the global snapshot against any tenant and
///     client overrides.
/// </summary>
public interface IModelCatalogRepository
{
    /// <summary>
    ///     Returns the catalog entries a client may pick from, with overrides already applied. Only the client's
    ///     own tenant's overrides are ever considered, so one customer's negotiated rates cannot be observed by
    ///     another.
    /// </summary>
    /// <param name="clientId">Client the catalog is being resolved for.</param>
    /// <param name="providerId">Restrict to a single catalog provider, or null for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<AiModelCatalogEntryDto>> GetEffectiveForClientAsync(
        Guid clientId,
        string? providerId = null,
        CancellationToken ct = default);

    /// <summary>Returns the distinct providers the catalog describes, for a browse-by-provider surface.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(string ProviderId, string ProviderName, int ModelCount)>> GetProvidersAsync(CancellationToken ct = default);
}
