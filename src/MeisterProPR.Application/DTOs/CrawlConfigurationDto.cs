namespace MeisterProPR.Application.DTOs;

/// <summary>Data transfer object for a crawl configuration.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID.</param>
/// <param name="OrganizationUrl">ADO organisation URL.</param>
/// <param name="ProjectId">ADO project ID.</param>
/// <param name="ReviewerId">
///     ADO identity GUID of the service account reviewer; null if not configured on the owning
///     client.
/// </param>
/// <param name="CrawlIntervalSeconds">Polling interval in seconds.</param>
/// <param name="IsActive">Whether the configuration is active.</param>
/// <param name="CreatedAt">When the configuration was created.</param>
public sealed record CrawlConfigurationDto(
    Guid Id,
    Guid ClientId,
    string OrganizationUrl,
    string ProjectId,
    Guid? ReviewerId,
    int CrawlIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt);
