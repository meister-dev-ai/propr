// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for per-client and per-crawl-config AI prompt overrides.</summary>
public interface IPromptOverrideRepository
{
    /// <summary>
    ///     Returns the override matching the given client, scope, crawl config, and prompt key; or
    ///     <see langword="null" /> if not found.
    /// </summary>
    Task<PromptOverride?> GetByScopeAsync(
        Guid clientId,
        PromptOverrideScope scope,
        Guid? crawlConfigId,
        string promptKey,
        CancellationToken ct = default);

    /// <summary>Returns the override with the given ID, or <see langword="null" /> if not found.</summary>
    Task<PromptOverride?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns all overrides for the given client, ordered by <c>CreatedAt</c> ascending.</summary>
    Task<IReadOnlyList<PromptOverride>> ListByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Persists a new override.</summary>
    Task AddAsync(PromptOverride promptOverride, CancellationToken ct = default);

    /// <summary>Persists changes to an existing override (e.g. updated text).</summary>
    Task UpdateAsync(PromptOverride promptOverride, CancellationToken ct = default);

    /// <summary>Removes the override with the given ID. Returns <see langword="false" /> if not found.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);
}
