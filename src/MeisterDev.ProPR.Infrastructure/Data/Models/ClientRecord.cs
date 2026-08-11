// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

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
    ///     Controls whether Code Insights collects quality facts for this client (finding records, type
    ///     tags, dispositions, misses, and memory keywords) and spends model tokens classifying them.
    ///     Defaults to <see langword="false" />: making an installation commercial changes nothing until a
    ///     client opts in. Collection is forward-only, so turning it on collects from that point and turning
    ///     it off stops further collection without removing what was already collected.
    ///     The commercial capability gate applies in addition to this flag; both must be open.
    /// </summary>
    public bool CodeInsightsCollectionEnabled { get; set; } = false;

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

    /// <summary>
    ///     Controls whether an automatic trigger reviews every pushed update to a pull request. Defaults to
    ///     <see langword="false" />: crawl and webhook activation review a pull request at the first revision they
    ///     see and leave later revisions alone, so a run of quick pushes costs one review instead of one per push.
    ///     Requested reviews are never affected.
    /// </summary>
    public bool ReviewEveryIncrementEnabled { get; set; } = false;

    /// <summary>
    ///     The natural language this client's reviewer-facing prose is written in, as an IETF BCP 47 language tag.
    ///     Defaults to <see cref="Domain.ValueObjects.ReviewOutputLanguage.Default" />. The language is never
    ///     detected from the pull request, so every surface of a review reads the same way. Fixed labels ProPR
    ///     renders around the model's prose stay English.
    /// </summary>
    public string OutputLanguage { get; set; } = ReviewOutputLanguage.Default;

    /// <summary>
    ///     Minimum severity a review finding must have for its comment to be published to the SCM provider.
    ///     Findings ranked below this threshold are retained in the persisted review result but not posted.
    ///     Rank, high to low: Error, Warning, Suggestion, Info. Defaults to
    ///     <see cref="Domain.Enums.CommentSeverity.Info" /> so every finding is published (current behavior).
    /// </summary>
    public CommentSeverity MinimumSeverityToPost { get; set; } = CommentSeverity.Info;

    /// <summary>
    ///     Severities whose published comments are posted already resolved, each carrying an explanatory note.
    ///     Empty by default so nothing is auto-resolved. Applied after the minimum-severity filter, so a finding
    ///     below <see cref="MinimumSeverityToPost" /> is never auto-resolved because it is never published.
    /// </summary>
    public IReadOnlyList<CommentSeverity> AutoResolveSeverities { get; set; } = [];

    /// <summary>
    ///     Controls whether findings in pre-existing code outside the pull request's changed lines are published
    ///     to the SCM provider. Defaults to <see langword="false" />, which publishes them carrying the label that
    ///     says where they are. Set to <see langword="true" /> to keep them off the pull request; they stay in the
    ///     persisted review result either way, and the published summary reports how many were held back.
    /// </summary>
    public bool WithholdOutOfScopeFindings { get; set; } = false;

    /// <summary>
    ///     Tags a runner must declare before this client's reviews may be offered to it, comma-separated.
    ///     Empty by default, which every runner satisfies.
    ///     <para>
    ///         Routing needs belong to the client rather than to one pull request: they come from the
    ///         repository's toolchain, its size, or where its code is allowed to be checked out, none of which
    ///         change between two pull requests. A runner is offered a job only when it declares every tag
    ///         named here, so adding one narrows the pool of eligible runners and can never widen it.
    ///     </para>
    /// </summary>
    public string RequiredRunnerTags { get; set; } = string.Empty;

    /// <summary>
    ///     Optional soft USD cap on this client's month-to-date review spend. When month-to-date spend reaches it,
    ///     new review jobs are held rather than started (running jobs finish). Null means no limit.
    /// </summary>
    public decimal? MonthlyBudgetSoftCapUsd { get; set; }

    /// <summary>
    ///     Optional hard USD cap on this client's month-to-date review spend. When month-to-date spend reaches it,
    ///     further model calls are cut. Null means no limit.
    /// </summary>
    public decimal? MonthlyBudgetHardCapUsd { get; set; }

    /// <summary>
    ///     Optional default soft USD cap applied to each pull request under this client (summed across the PR's
    ///     review jobs). When reached, new jobs for that PR are held. Null means no limit.
    /// </summary>
    public decimal? PullRequestBudgetSoftCapUsd { get; set; }

    /// <summary>
    ///     Optional default hard USD cap applied to each pull request under this client (summed across the PR's
    ///     review jobs). When reached, further model calls are cut. Null means no limit.
    /// </summary>
    public decimal? PullRequestBudgetHardCapUsd { get; set; }

    /// <summary>
    ///     Optional default soft USD cap applied to each PR increment (a single review job) under this client. When a
    ///     running job's spend reaches it, the job stops scanning further files and concludes with a synthesis that
    ///     notes the review was soft-capped. Null means no limit.
    /// </summary>
    public decimal? IncrementBudgetSoftCapUsd { get; set; }

    /// <summary>
    ///     Optional default hard USD cap applied to each PR increment (a single review job) under this client. When
    ///     reached, further model calls are cut. Null means no limit.
    /// </summary>
    public decimal? IncrementBudgetHardCapUsd { get; set; }

    public TenantRecord? Tenant { get; set; }

    public ICollection<ClientScmConnectionRecord> ScmConnections { get; set; } = [];

    public ICollection<ProviderConnectionAuditEntryRecord> ProviderConnectionAuditEntries { get; set; } = [];

    public ICollection<ClientReviewerIdentityRecord> ReviewerIdentities { get; set; } = [];

    /// <summary>Ordered per-client review-pass list; each entry runs one additional multi-pass union pass.</summary>
    public ICollection<ClientReviewPassRecord> ReviewPasses { get; set; } = [];

    public ICollection<ClientPurposeLogicalModelRecord> PurposeLogicalModels { get; set; } = [];

    public ICollection<CrawlConfigurationRecord> CrawlConfigurations { get; set; } = [];
}
