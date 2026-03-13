namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a per-client ADO crawl target.</summary>
public sealed class CrawlConfigurationRecord
{
    public bool IsActive { get; set; }
    public ClientRecord Client { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public Guid ClientId { get; set; }
    public Guid Id { get; set; }
    public int CrawlIntervalSeconds { get; set; }
    public string OrganizationUrl { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
}