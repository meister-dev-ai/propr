// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a repository-scope filter on a crawl configuration.</summary>
public sealed class CrawlRepoFilterRecord
{
    public Guid Id { get; set; }
    public Guid CrawlConfigurationId { get; set; }
    public string? SourceProvider { get; set; }
    public string? CanonicalSourceRef { get; set; }
    public string? DisplayName { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
    public string[] TargetBranchPatterns { get; set; } = [];

    public CrawlConfigurationRecord? CrawlConfiguration { get; set; }
}
