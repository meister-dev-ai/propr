// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

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
    public Guid? OrganizationScopeId { get; set; }
    public ProCursorSourceScopeMode ProCursorSourceScopeMode { get; set; } = ProCursorSourceScopeMode.AllClientSources;

    /// <summary>Optional repository filter (narrows crawl to a specific repository).</summary>
    public string? RepositoryId { get; set; }

    /// <summary>Optional branch filter (narrows crawl to a specific branch).</summary>
    public string? BranchFilter { get; set; }

    /// <summary>Optional repository-scope filters for this crawl configuration.</summary>
    public ICollection<CrawlRepoFilterRecord> RepoFilters { get; set; } = [];

    public ClientAdoOrganizationScopeRecord? OrganizationScope { get; set; }

    public ICollection<CrawlConfigurationProCursorSourceRecord> ProCursorSources { get; set; } = [];
}
