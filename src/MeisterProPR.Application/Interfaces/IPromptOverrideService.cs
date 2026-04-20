// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Business logic for managing per-client and per-crawl-config AI prompt overrides.</summary>
public interface IPromptOverrideService
{
    /// <summary>
    ///     Resolves the effective override text for the given prompt key using the three-level lookup chain:
    ///     crawl-config scope → client scope → (null, meaning use the global hardcoded default).
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="crawlConfigId">Crawl config identifier. Pass <see langword="null" /> to skip crawl-config scope.</param>
    /// <param name="promptKey">Named prompt segment.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The override text, or <see langword="null" /> if no override is defined.</returns>
    Task<string?> GetOverrideAsync(
        Guid clientId,
        Guid? crawlConfigId,
        string promptKey,
        CancellationToken ct = default);

    /// <summary>Lists all overrides for the given client.</summary>
    Task<IReadOnlyList<PromptOverrideDto>> ListByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Creates a new prompt override and returns its DTO.</summary>
    Task<PromptOverrideDto> CreateAsync(
        Guid clientId,
        PromptOverrideScope scope,
        Guid? crawlConfigId,
        string promptKey,
        string overrideText,
        CancellationToken ct = default);

    /// <summary>
    ///     Updates the override text for the given override. Returns the updated DTO, or <see langword="null" /> if not
    ///     found or not owned by the client.
    /// </summary>
    Task<PromptOverrideDto?> UpdateAsync(Guid clientId, Guid id, string overrideText, CancellationToken ct = default);

    /// <summary>
    ///     Deletes the override with the given ID. Returns <see langword="false" /> if not found or not owned by the
    ///     client.
    /// </summary>
    Task<bool> DeleteAsync(Guid clientId, Guid id, CancellationToken ct = default);
}
