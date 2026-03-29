using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed crawl configuration repository.</summary>
public sealed class CrawlConfigurationRepository(MeisterProPRDbContext dbContext)
    : ICrawlConfigurationRepository
{
    private static CrawlConfigurationDto ToDto(CrawlConfigurationRecord c, Guid? reviewerId) =>
        new(
            c.Id,
            c.ClientId,
            c.OrganizationUrl,
            c.ProjectId,
            reviewerId,
            c.CrawlIntervalSeconds,
            c.IsActive,
            c.CreatedAt,
            c.RepoFilters
                .Select(f => new CrawlRepoFilterDto(f.Id, f.RepositoryName, f.TargetBranchPatterns))
                .ToList()
                .AsReadOnly());

    private IQueryable<CrawlConfigurationRecord> BaseQuery() =>
        dbContext.CrawlConfigurations
            .Include(c => c.Client)
            .Include(c => c.RepoFilters);

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(Guid configId, Guid clientId, bool isActive, CancellationToken ct = default)
    {
        var record = await dbContext.CrawlConfigurations
            .FirstOrDefaultAsync(c => c.Id == configId && c.ClientId == clientId, ct);
        if (record is null)
        {
            return false;
        }

        record.IsActive = isActive;
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationDto> AddAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        int crawlIntervalSeconds,
        CancellationToken ct = default)
    {
        var record = new CrawlConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            OrganizationUrl = organizationUrl,
            ProjectId = projectId,
            CrawlIntervalSeconds = crawlIntervalSeconds,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.CrawlConfigurations.Add(record);
        await dbContext.SaveChangesAsync(ct);

        // Populate ReviewerId from the owning client record.
        var clientReviewerId = await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.ReviewerId)
            .FirstOrDefaultAsync(ct);

        return new CrawlConfigurationDto(
            record.Id,
            record.ClientId,
            record.OrganizationUrl,
            record.ProjectId,
            clientReviewerId,
            record.CrawlIntervalSeconds,
            record.IsActive,
            record.CreatedAt,
            []);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .Where(c => c.IsActive)
            .ToListAsync(ct);
        return records.Select(c => ToDto(c, c.Client.ReviewerId)).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public Task<bool> ExistsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        CancellationToken ct = default)
    {
        return dbContext.CrawlConfigurations.AnyAsync(
            c => c.ClientId == clientId &&
                 c.OrganizationUrl == organizationUrl &&
                 c.ProjectId == projectId,
            ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default)
    {
        var record = await dbContext.CrawlConfigurations
            .FirstOrDefaultAsync(c => c.Id == configId && c.ClientId == clientId, ct);
        if (record is null)
        {
            return false;
        }

        dbContext.CrawlConfigurations.Remove(record);
        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientAsync(
        Guid clientId,
        CancellationToken ct = default)
    {
        var records = await this.BaseQuery()
            .Where(c => c.ClientId == clientId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return records.Select(c => ToDto(c, c.Client.ReviewerId)).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientIdsAsync(
        IEnumerable<Guid> clientIds, CancellationToken ct = default)
    {
        var ids = clientIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var records = await this.BaseQuery()
            .Where(c => ids.Contains(c.ClientId))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        return records.Select(c => ToDto(c, c.Client.ReviewerId)).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<CrawlConfigurationDto?> GetByIdAsync(Guid configId, CancellationToken ct = default)
    {
        var record = await this.BaseQuery()
            .Where(c => c.Id == configId)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : ToDto(record, record.Client.ReviewerId);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Guid configId,
        int? crawlIntervalSeconds,
        bool? isActive,
        Guid? ownerClientId,
        CancellationToken ct = default)
    {
        var query = dbContext.CrawlConfigurations.Where(c => c.Id == configId);
        if (ownerClientId.HasValue)
        {
            query = query.Where(c => c.ClientId == ownerClientId.Value);
        }

        var record = await query.FirstOrDefaultAsync(ct);
        if (record is null)
        {
            return false;
        }

        if (crawlIntervalSeconds.HasValue)
        {
            record.CrawlIntervalSeconds = crawlIntervalSeconds.Value;
        }

        if (isActive.HasValue)
        {
            record.IsActive = isActive.Value;
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateRepoFiltersAsync(
        Guid configId,
        IReadOnlyList<CrawlRepoFilterDto> filters,
        CancellationToken ct = default)
    {
        var config = await dbContext.CrawlConfigurations
            .Include(c => c.RepoFilters)
            .FirstOrDefaultAsync(c => c.Id == configId, ct);
        if (config is null)
        {
            return false;
        }

        // Full-replacement semantics: remove all existing filters, then insert new ones.
        dbContext.CrawlRepoFilters.RemoveRange(config.RepoFilters);

        foreach (var filter in filters)
        {
            config.RepoFilters.Add(new CrawlRepoFilterRecord
            {
                Id = Guid.NewGuid(),
                CrawlConfigurationId = configId,
                RepositoryName = filter.RepositoryName,
                TargetBranchPatterns = filter.TargetBranchPatterns.ToArray(),
            });
        }

        await dbContext.SaveChangesAsync(ct);
        return true;
    }
}
