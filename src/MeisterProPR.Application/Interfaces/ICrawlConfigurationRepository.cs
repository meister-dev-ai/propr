using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Repository for per-client ADO crawl configurations.</summary>
public interface ICrawlConfigurationRepository
{
    /// <summary>Adds a new crawl configuration for the given client.</summary>
    Task<CrawlConfigurationDto> AddAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        int crawlIntervalSeconds,
        CancellationToken ct = default);

    /// <summary>Deletes a crawl configuration. Returns false if not found or not owned by clientId.</summary>
    Task<bool> DeleteAsync(Guid configId, Guid clientId, CancellationToken ct = default);

    /// <summary>Returns true if a configuration with the same org/project already exists for the client.</summary>
    Task<bool> ExistsAsync(Guid clientId, string organizationUrl, string projectId, CancellationToken ct = default);

    /// <summary>Returns all active crawl configurations across all clients.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetAllActiveAsync(CancellationToken ct = default);

    /// <summary>Returns all crawl configurations for a specific client.</summary>
    Task<IReadOnlyList<CrawlConfigurationDto>> GetByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Enables or disables a crawl configuration. Returns false if not found or not owned by clientId.</summary>
    Task<bool> SetActiveAsync(Guid configId, Guid clientId, bool isActive, CancellationToken ct = default);
}