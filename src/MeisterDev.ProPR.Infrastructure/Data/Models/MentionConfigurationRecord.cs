// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One client's declaration that it answers `@`-mentions on a set of repositories within a provider
///     project.
/// </summary>
/// <remarks>
///     Deliberately separate from the crawl configuration rather than derived from it. Answering a question
///     and reviewing a pull request are different undertakings: a client may want one without the other, a
///     client configured only by webhook has no crawl configuration to inherit from, and a crawl
///     configuration says which pull requests to review, which is not the same statement as which
///     conversations to join.
/// </remarks>
public sealed class MentionConfigurationRecord
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The client that answers, and that pays for the answers.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Owning client record.</summary>
    public ClientRecord Client { get; set; } = null!;

    /// <summary>Normalized source-control provider family.</summary>
    public ScmProvider Provider { get; set; } = ScmProvider.AzureDevOps;

    /// <summary>Provider scope path the project lives under.</summary>
    public string OrganizationUrl { get; set; } = string.Empty;

    /// <summary>Provider project, workspace, or namespace key.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>Shortest gap between two scans of this configuration.</summary>
    public int ScanIntervalSeconds { get; set; }

    /// <summary>Whether this configuration is scanned.</summary>
    public bool IsActive { get; set; }

    /// <summary>When the configuration was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The repositories this configuration answers mentions on. Never empty.</summary>
    public ICollection<MentionRepoFilterRecord> RepoFilters { get; set; } = [];
}
