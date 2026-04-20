// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IProCursorKnowledgeSourceRepository" />.
/// </summary>
public sealed class ProCursorKnowledgeSourceRepository(MeisterProPRDbContext db) : IProCursorKnowledgeSourceRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorKnowledgeSource>> ListEnabledAsync(CancellationToken ct = default)
    {
        return await db.ProCursorKnowledgeSources
            .Include(source => source.TrackedBranches)
            .Where(source => source.IsEnabled)
            .OrderBy(source => source.DisplayName)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorKnowledgeSource>> ListByClientAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        return await db.ProCursorKnowledgeSources
            .Include(source => source.TrackedBranches)
            .Where(source => source.ClientId == clientId)
            .OrderBy(source => source.DisplayName)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorKnowledgeSource?> GetBySourceIdAsync(Guid sourceId, CancellationToken ct = default)
    {
        return await db.ProCursorKnowledgeSources
            .Include(source => source.TrackedBranches)
            .FirstOrDefaultAsync(source => source.Id == sourceId, ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorKnowledgeSource?> GetByIdAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default)
    {
        return await db.ProCursorKnowledgeSources
            .Include(source => source.TrackedBranches)
            .FirstOrDefaultAsync(source => source.ClientId == clientId && source.Id == sourceId, ct);
    }

    /// <inheritdoc />
    public async Task<ProCursorKnowledgeSource?> GetByRepositoryAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        CancellationToken ct = default)
    {
        return await db.ProCursorKnowledgeSources
            .Include(source => source.TrackedBranches)
            .FirstOrDefaultAsync(
                source =>
                    source.ClientId == clientId &&
                    source.ProviderScopePath == organizationUrl &&
                    source.ProviderProjectKey == projectId &&
                    source.RepositoryId == repositoryId,
                ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(
        Guid clientId,
        ProCursorSourceKind sourceKind,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string? rootPath,
        CancellationToken ct = default)
    {
        var normalizedRootPath = string.IsNullOrWhiteSpace(rootPath) ? null : rootPath.Trim();

        return await db.ProCursorKnowledgeSources.AnyAsync(
            source =>
                source.ClientId == clientId &&
                source.SourceKind == sourceKind &&
                source.ProviderScopePath == organizationUrl &&
                source.ProviderProjectKey == projectId &&
                source.RepositoryId == repositoryId &&
                source.RootPath == normalizedRootPath,
            ct);
    }

    /// <inheritdoc />
    public async Task AddAsync(ProCursorKnowledgeSource source, CancellationToken ct = default)
    {
        db.ProCursorKnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdateAsync(ProCursorKnowledgeSource source, CancellationToken ct = default)
    {
        db.ProCursorKnowledgeSources.Update(source);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
    {
        var branch = await db.ProCursorTrackedBranches
            .Join(
                db.ProCursorKnowledgeSources,
                branch => branch.KnowledgeSourceId,
                source => source.Id,
                (branch, source) => new { branch, source })
            .Where(item =>
                item.source.ClientId == clientId &&
                item.source.Id == sourceId &&
                item.branch.Id == trackedBranchId)
            .Select(item => item.branch)
            .FirstOrDefaultAsync(ct);

        if (branch is null)
        {
            return false;
        }

        db.ProCursorTrackedBranches.Remove(branch);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
