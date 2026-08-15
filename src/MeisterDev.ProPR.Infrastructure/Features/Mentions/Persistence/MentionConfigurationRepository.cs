// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Mentions.Persistence;

/// <summary>EF Core persistence for <see cref="IMentionConfigurationRepository" />.</summary>
public sealed class MentionConfigurationRepository(MeisterProPRDbContext dbContext)
    : IMentionConfigurationRepository
{
    /// <inheritdoc />
    public async Task<MentionConfigurationDto> AddAsync(
        Guid clientId,
        ScmProvider provider,
        string providerScopePath,
        string providerProjectKey,
        int scanIntervalSeconds,
        IReadOnlyList<MentionRepoFilterDto> repoFilters,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(repoFilters);

        if (repoFilters.Count == 0)
        {
            throw new ArgumentException(
                "A mention configuration must name at least one repository.",
                nameof(repoFilters));
        }

        var record = new MentionConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            Provider = provider,
            OrganizationUrl = providerScopePath,
            ProjectId = providerProjectKey,
            ScanIntervalSeconds = scanIntervalSeconds,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            RepoFilters = repoFilters.Select(filter => ToRecord(filter, DateTimeOffset.UtcNow)).ToList(),
        };

        dbContext.MentionConfigurations.Add(record);
        await dbContext.SaveChangesAsync(ct);
        return ToDto(record);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MentionConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default)
    {
        var records = await dbContext.MentionConfigurations
            .AsNoTracking()
            .Include(c => c.RepoFilters)
            .Where(c => c.IsActive)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MentionConfigurationDto>> GetAllAsync(CancellationToken ct = default)
    {
        var records = await dbContext.MentionConfigurations
            .AsNoTracking()
            .Include(c => c.RepoFilters)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MentionConfigurationDto>> GetByClientAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var records = await dbContext.MentionConfigurations
            .AsNoTracking()
            .Include(c => c.RepoFilters)
            .Where(c => c.ClientId == clientId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        return records.Select(ToDto).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<MentionConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default)
    {
        var record = await dbContext.MentionConfigurations
            .AsNoTracking()
            .Include(c => c.RepoFilters)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);

        return record is null ? null : ToDto(record);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid configId,
        Guid clientId,
        int? scanIntervalSeconds,
        bool? isActive,
        IReadOnlyList<MentionRepoFilterDto>? repoFilters,
        CancellationToken ct = default)
    {
        if (repoFilters is { Count: 0 })
        {
            throw new ArgumentException(
                "A mention configuration must name at least one repository.",
                nameof(repoFilters));
        }

        var record = await dbContext.MentionConfigurations
            .Include(c => c.RepoFilters)
            .FirstOrDefaultAsync(c => c.Id == configId && c.ClientId == clientId, ct);

        if (record is null)
        {
            return false;
        }

        if (scanIntervalSeconds is { } interval)
        {
            record.ScanIntervalSeconds = interval;
        }

        if (isActive is { } active)
        {
            record.IsActive = active;
        }

        if (repoFilters is not null)
        {
            // Replaced wholesale rather than merged. A repository the operator removed must stop being
            // answered on, and reconciling by identifier would leave a stale row behind whenever the
            // editing screen sends a list it built from scratch.
            //
            // The claim time is the exception: a repository that survives the edit keeps the one it had.
            // Stamping every row afresh would move the floor forward on an unrelated edit, and any
            // question asked since the last scan would fall below it and never be answered.
            var claimedBefore = record.RepoFilters.ToDictionary(
                existing => existing.RepositoryId,
                existing => existing.ClaimedAt,
                StringComparer.OrdinalIgnoreCase);

            dbContext.MentionRepoFilters.RemoveRange(record.RepoFilters);
            record.RepoFilters = repoFilters
                .Select(filter => ToRecord(
                    filter,
                    claimedBefore.TryGetValue(filter.RepositoryId, out var claimedAt)
                        ? claimedAt
                        : DateTimeOffset.UtcNow))
                .ToList();
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default)
    {
        var record = await dbContext.MentionConfigurations
            .FirstOrDefaultAsync(c => c.Id == configId && c.ClientId == clientId, ct);

        if (record is null)
        {
            return false;
        }

        dbContext.MentionConfigurations.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    private static MentionRepoFilterRecord ToRecord(MentionRepoFilterDto filter, DateTimeOffset claimedAt)
    {
        return new MentionRepoFilterRecord
        {
            Id = Guid.NewGuid(),
            RepositoryId = filter.RepositoryId,
            CanonicalSourceRef = filter.CanonicalSourceRef,
            DisplayName = filter.DisplayName,
            SourceProvider = filter.SourceProvider,
            ClaimedAt = claimedAt,
        };
    }

    private static MentionConfigurationDto ToDto(MentionConfigurationRecord record)
    {
        return new MentionConfigurationDto(
            record.Id,
            record.ClientId,
            record.Provider,
            record.OrganizationUrl,
            record.ProjectId,
            record.ScanIntervalSeconds,
            record.IsActive,
            record.CreatedAt,
            record.RepoFilters
                .Select(filter => new MentionRepoFilterDto(
                    filter.Id,
                    filter.RepositoryId,
                    filter.CanonicalSourceRef,
                    filter.DisplayName,
                    filter.SourceProvider,
                    filter.ClaimedAt))
                .ToList()
                .AsReadOnly());
    }
}
