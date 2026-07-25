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

    /// <summary>
    ///     Returns the catalog as it applies to a tenant, with that tenant's own overrides applied but no client
    ///     override. This is what a tenant administrator browses when choosing a model to negotiate a rate for;
    ///     without it they would have to know a provider and model identifier by heart, which is the error the
    ///     catalog exists to remove.
    /// </summary>
    /// <param name="tenantId">Tenant the catalog is being resolved for.</param>
    /// <param name="providerId">Restrict to a single catalog provider, or null for all of them.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<AiModelCatalogEntryDto>> GetEffectiveForTenantAsync(
        Guid tenantId,
        string? providerId = null,
        CancellationToken ct = default);

    /// <summary>Returns the distinct providers the catalog describes, for a browse-by-provider surface.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<(string ProviderId, string ProviderName, int ModelCount)>> GetProvidersAsync(CancellationToken ct = default);

    /// <summary>Returns a tenant's own override rows, which is what its editor lists and edits.</summary>
    /// <param name="tenantId">Tenant whose overrides are read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<AiModelCatalogOverrideDto>> GetTenantOverridesAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Records or replaces a tenant's override for one model. A null price means inherit, so clearing a field
    ///     is how a tenant returns to list pricing for it.
    /// </summary>
    /// <param name="tenantId">Tenant the override belongs to.</param>
    /// <param name="override">The override to store.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpsertTenantOverrideAsync(Guid tenantId, AiModelCatalogOverrideDto @override, CancellationToken ct = default);

    /// <summary>
    ///     Defines a model the snapshot does not describe, scoped to one tenant, so a private fine-tune, a release
    ///     newer than the bundled catalog, or a self-hosted model becomes selectable and budgeted.
    /// </summary>
    /// <param name="tenantId">Tenant the definition belongs to.</param>
    /// <param name="definition">The model's own facts, since there is no snapshot entry to inherit them from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Exceptions.ModelCatalogDefinitionConflictException">
    ///     The catalog already describes this model, so its capabilities would come from the snapshot and the
    ///     definition's own values would be ignored. A pricing override is the right instrument instead.
    /// </exception>
    Task UpsertTenantModelDefinitionAsync(
        Guid tenantId,
        AiModelCatalogDefinitionDto definition,
        CancellationToken ct = default);

    /// <summary>Removes a tenant's override for one model, returning it to the global snapshot's values.</summary>
    /// <param name="tenantId">Tenant the override belongs to.</param>
    /// <param name="providerId">Catalog provider identifier.</param>
    /// <param name="remoteModelId">Model identifier as the provider knows it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when an override was removed; false when none existed.</returns>
    Task<bool> DeleteTenantOverrideAsync(Guid tenantId, string providerId, string remoteModelId, CancellationToken ct = default);
}
