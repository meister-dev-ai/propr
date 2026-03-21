using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed crawl configuration repository.</summary>
public sealed class PostgresCrawlConfigurationRepository(MeisterProPRDbContext dbContext)
    : ICrawlConfigurationRepository
{
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
            record.CreatedAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CrawlConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default)
    {
        return await dbContext.CrawlConfigurations
            .Where(c => c.IsActive)
            .Select(c => new CrawlConfigurationDto(
                c.Id,
                c.ClientId,
                c.OrganizationUrl,
                c.ProjectId,
                c.Client.ReviewerId,
                c.CrawlIntervalSeconds,
                c.IsActive,
                c.CreatedAt))
            .ToListAsync(ct);
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
        return await dbContext.CrawlConfigurations
            .Where(c => c.ClientId == clientId)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CrawlConfigurationDto(
                c.Id,
                c.ClientId,
                c.OrganizationUrl,
                c.ProjectId,
                c.Client.ReviewerId,
                c.CrawlIntervalSeconds,
                c.IsActive,
                c.CreatedAt))
            .ToListAsync(ct);
    }
}
