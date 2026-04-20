// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.PromptCustomization.Services;

/// <summary>
///     Manages per-client and per-crawl-config AI review prompt overrides.
///     The resolution chain is: crawl-config scope → client scope → global hardcoded default (null return).
/// </summary>
public sealed class PromptOverrideService(IPromptOverrideRepository repository) : IPromptOverrideService
{
    /// <inheritdoc />
    public async Task<string?> GetOverrideAsync(
        Guid clientId,
        Guid? crawlConfigId,
        string promptKey,
        CancellationToken ct = default)
    {
        if (crawlConfigId.HasValue)
        {
            var crawlOverride = await repository.GetByScopeAsync(
                clientId,
                PromptOverrideScope.CrawlConfigScope,
                crawlConfigId.Value,
                promptKey,
                ct);

            if (crawlOverride is not null)
            {
                return crawlOverride.OverrideText;
            }
        }

        var clientOverride = await repository.GetByScopeAsync(
            clientId,
            PromptOverrideScope.ClientScope,
            null,
            promptKey,
            ct);

        return clientOverride?.OverrideText;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PromptOverrideDto>> ListByClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var entities = await repository.ListByClientAsync(clientId, ct);
        return entities.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<PromptOverrideDto> CreateAsync(
        Guid clientId,
        PromptOverrideScope scope,
        Guid? crawlConfigId,
        string promptKey,
        string overrideText,
        CancellationToken ct = default)
    {
        var entity = new PromptOverride(
            Guid.NewGuid(),
            clientId,
            crawlConfigId,
            scope,
            promptKey,
            overrideText);

        await repository.AddAsync(entity, ct);
        return ToDto(entity);
    }

    /// <inheritdoc />
    public async Task<PromptOverrideDto?> UpdateAsync(
        Guid clientId,
        Guid id,
        string overrideText,
        CancellationToken ct = default)
    {
        var entity = await repository.GetByIdAsync(id, ct);
        if (entity is null || entity.ClientId != clientId)
        {
            return null;
        }

        entity.UpdateText(overrideText);
        await repository.UpdateAsync(entity, ct);
        return ToDto(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid clientId, Guid id, CancellationToken ct = default)
    {
        var entity = await repository.GetByIdAsync(id, ct);
        if (entity is null || entity.ClientId != clientId)
        {
            return false;
        }

        return await repository.DeleteAsync(id, ct);
    }

    private static PromptOverrideDto ToDto(PromptOverride entity)
    {
        return new PromptOverrideDto(
            entity.Id,
            entity.ClientId,
            entity.CrawlConfigId,
            entity.Scope,
            entity.PromptKey,
            entity.OverrideText,
            entity.CreatedAt,
            entity.UpdatedAt);
    }
}
