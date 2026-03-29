namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a repository-scope filter on a crawl configuration.</summary>
public sealed class CrawlRepoFilterRecord
{
    public Guid Id { get; set; }
    public Guid CrawlConfigurationId { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
    public string[] TargetBranchPatterns { get; set; } = [];

    public CrawlConfigurationRecord? CrawlConfiguration { get; set; }
}
