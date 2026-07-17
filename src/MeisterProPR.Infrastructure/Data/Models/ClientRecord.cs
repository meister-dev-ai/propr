// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a registered API client.</summary>
public sealed class ClientRecord
{
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     Determines how the reviewer behaves when automatically resolving its own comment threads.
    ///     Defaults to <see cref="Domain.Enums.CommentResolutionBehavior.Silent" />.
    /// </summary>
    public CommentResolutionBehavior CommentResolutionBehavior { get; set; } = CommentResolutionBehavior.Silent;

    /// <summary>Optional custom AI system message for this client.</summary>
    public string? CustomSystemMessage { get; set; }

    /// <summary>
    ///     Optional default review pipeline profile for newly created review jobs.
    ///     When null, the system baseline profile is used.
    /// </summary>
    public string? DefaultReviewPipelineProfileId { get; set; }

    /// <summary>
    ///     Timestamp of the most recent explicit default review pipeline profile change.
    ///     Null when the client has never stored an explicit profile override.
    /// </summary>
    public DateTimeOffset? DefaultReviewPipelineProfileUpdatedAtUtc { get; set; }

    /// <summary>
    ///     Controls whether newly generated review comments are published back to the SCM provider.
    ///     Defaults to <see langword="true" /> so existing clients continue using visible review publication.
    /// </summary>
    public bool ScmCommentPostingEnabled { get; set; } = true;

    /// <summary>
    ///     Controls whether evidence-backed local verification escalates conservatively-withheld claims for this client.
    ///     Defaults to <see langword="false" /> so new clients opt in explicitly.
    /// </summary>
    public bool EnableEvidenceBackedVerification { get; set; } = false;

    /// <summary>
    ///     When set, review-comment screening uses language-robust structured signals + evidence routing
    ///     (self-report / classifier + demote-don't-delete) instead of the English phrase-list filters.
    /// </summary>
    public bool EnableLanguageRobustScreening { get; set; } = false;

    /// <summary>
    ///     Controls whether multi-pass union generation runs during review for this client.
    ///     Defaults to <see langword="false" /> so new clients opt in explicitly.
    /// </summary>
    public bool EnableMultiPassUnion { get; set; } = false;

    /// <summary>
    ///     Reasoning effort applied to the implicit tier baseline review pass for this client.
    ///     Defaults to <see cref="Domain.Enums.ReviewReasoningEffort.None" /> so no effort is sent until a user
    ///     opts in (behavior and cost unchanged). Per-additional-pass effort lives on each
    ///     <see cref="ClientReviewPassRecord" />.
    /// </summary>
    public ReviewReasoningEffort BaselineReasoningEffort { get; set; } = ReviewReasoningEffort.None;

    /// <summary>
    ///     Controls whether the work items (Azure DevOps) or issues (GitHub, GitLab, Forgejo) linked to a
    ///     pull request are fetched and included in the review context for this client. Defaults to
    ///     <see langword="true" /> so the review can judge changes against their intended direction.
    /// </summary>
    public bool IncludeLinkedItemsInContext { get; set; } = true;

    public TenantRecord? Tenant { get; set; }

    public ICollection<ClientScmConnectionRecord> ScmConnections { get; set; } = [];

    public ICollection<ProviderConnectionAuditEntryRecord> ProviderConnectionAuditEntries { get; set; } = [];

    public ICollection<ClientReviewerIdentityRecord> ReviewerIdentities { get; set; } = [];

    /// <summary>Ordered per-client review-pass list; each entry runs one additional multi-pass union pass.</summary>
    public ICollection<ClientReviewPassRecord> ReviewPasses { get; set; } = [];

    public ICollection<CrawlConfigurationRecord> CrawlConfigurations { get; set; } = [];
}
