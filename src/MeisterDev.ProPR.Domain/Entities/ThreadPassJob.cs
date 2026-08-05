// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     One pass over a pull request's reviewer-owned comment threads: resolve what the developer fixed and
///     answer what they asked. Subordinate to the pull request and independent of any review job, because the
///     conversation and the file review run on separate cadences.
/// </summary>
/// <remarks>
///     State machine: <see cref="ThreadPassJobStatus.Pending" /> →
///     <see cref="ThreadPassJobStatus.Processing" /> → <see cref="ThreadPassJobStatus.Completed" /> |
///     <see cref="ThreadPassJobStatus.Failed" /> | <see cref="ThreadPassJobStatus.Cancelled" />. A failed
///     attempt returns the row to <see cref="ThreadPassJobStatus.Pending" /> while attempts remain; the row
///     only becomes terminally failed once <see cref="MaxAttempts" /> are spent, and it is never deleted.
/// </remarks>
public sealed class ThreadPassJob
{
    /// <summary>How many times one pass may be attempted before it stops trying.</summary>
    public const int MaxAttempts = 3;

    /// <summary>
    ///     How long a failed attempt waits before the pass may be attempted again. Without it a provider that
    ///     is down for a few seconds spends every attempt in those seconds, because the scan worker re-offers a
    ///     pending row on every tick and the row returns to pending the moment an attempt fails.
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);

    private ThreadPassJob()
    {
        this.OrganizationUrl = string.Empty;
        this.ProjectId = string.Empty;
        this.RepositoryId = string.Empty;
        this.RevisionKey = string.Empty;
        this.TriggerKey = string.Empty;
    }

    /// <summary>Creates a pending thread pass for one pull request at one revision.</summary>
    /// <param name="id">Unique identifier.</param>
    /// <param name="clientId">The client that owns the pull request.</param>
    /// <param name="organizationUrl">Provider scope path the pull request lives under.</param>
    /// <param name="projectId">Provider project, workspace, or namespace key.</param>
    /// <param name="repositoryId">Provider-native repository identifier.</param>
    /// <param name="pullRequestId">Provider pull request number.</param>
    /// <param name="iterationId">The iteration the pass reads the pull request at.</param>
    /// <param name="revisionKey">The stored revision key the pass runs at.</param>
    /// <param name="triggerKey">
    ///     What made this pass due: the revision key together with the observed non-reviewer comment counts.
    ///     Two passes that carry the same value would do the same work, so the second is never created.
    /// </param>
    public ThreadPassJob(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        string revisionKey,
        string triggerKey)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentOutOfRangeException.ThrowIfLessThan(pullRequestId, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterationId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(revisionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerKey);

        this.Id = id;
        this.ClientId = clientId;
        this.OrganizationUrl = organizationUrl;
        this.ProjectId = projectId;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.IterationId = iterationId;
        this.RevisionKey = revisionKey;
        this.TriggerKey = triggerKey;
        this.Provider = ScmProvider.AzureDevOps;
        this.HostBaseUrl = new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl).HostBaseUrl;
        this.RepositoryOwnerOrNamespace = projectId;
        this.RepositoryProjectPath = projectId;
        this.CodeReviewPlatformKind = CodeReviewPlatformKind.PullRequest;
        this.ExternalCodeReviewId = pullRequestId.ToString();
        this.Status = ThreadPassJobStatus.Pending;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>The client that owns the pull request.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Provider scope path the pull request lives under.</summary>
    public string OrganizationUrl { get; init; }

    /// <summary>Provider project, workspace, or namespace key.</summary>
    public string ProjectId { get; init; }

    /// <summary>Provider-native repository identifier.</summary>
    public string RepositoryId { get; init; }

    /// <summary>Provider pull request number.</summary>
    public int PullRequestId { get; init; }

    /// <summary>The iteration the pass reads the pull request at.</summary>
    public int IterationId { get; init; }

    /// <summary>The stored revision key this pass runs at.</summary>
    public string RevisionKey { get; init; }

    /// <summary>The trigger state that created this pass, so the same state never creates a second one.</summary>
    public string TriggerKey { get; init; }

    /// <summary>Normalized source-control provider family.</summary>
    public ScmProvider Provider { get; private set; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host base URL.</summary>
    public string? HostBaseUrl { get; private set; }

    /// <summary>Provider-neutral repository owner, namespace, or project scope.</summary>
    public string? RepositoryOwnerOrNamespace { get; private set; }

    /// <summary>Provider-neutral repository project path.</summary>
    public string? RepositoryProjectPath { get; private set; }

    /// <summary>Native code-review platform kind.</summary>
    public CodeReviewPlatformKind CodeReviewPlatformKind { get; private set; } = CodeReviewPlatformKind.PullRequest;

    /// <summary>Provider-native external review identifier.</summary>
    public string? ExternalCodeReviewId { get; private set; }

    /// <summary>Current status.</summary>
    public ThreadPassJobStatus Status { get; set; }

    /// <summary>How many times this pass has been attempted.</summary>
    public int AttemptCount { get; set; }

    /// <summary>When the pass was queued.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the most recent attempt began, if any.</summary>
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>
    ///     The earliest moment the next attempt may start, or <see langword="null" /> when the pass may run at
    ///     once. Set when an attempt fails, so retries are spaced rather than spent all at once.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>When the pass reached a terminal status, if it has.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Why the last attempt failed, if it did.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The AI connection whose model answered this pass, recorded when the runtime was resolved.</summary>
    public Guid? AiConnectionId { get; private set; }

    /// <summary>The model that answered this pass, recorded when the runtime was resolved.</summary>
    public string? AiModel { get; private set; }

    /// <summary>Input tokens this pass has spent across every thread it evaluated.</summary>
    public long TotalInputTokens { get; private set; }

    /// <summary>Output tokens this pass has spent across every thread it evaluated.</summary>
    public long TotalOutputTokens { get; private set; }

    /// <summary>
    ///     Estimated USD cost of everything this pass spent, or <see langword="null" /> while nothing priced has
    ///     been recorded. The per-pull-request and per-increment budget scopes read this column, which is why the
    ///     pass carries its own total rather than borrowing a review job's.
    /// </summary>
    public decimal? TotalEstimatedCostUsd { get; private set; }

    /// <summary>True when some recorded spend had no known price, so the total is a lower bound.</summary>
    public bool CostIsApproximate { get; private set; }

    /// <summary>The budget scope that blocked this pass, if one did.</summary>
    public BudgetScopeKind? BudgetBlockScope { get; private set; }

    /// <summary>Whether the cap that blocked this pass was a soft or a hard one.</summary>
    public BudgetCapKind? BudgetBlockCapKind { get; private set; }

    /// <summary>The cap the blocked scope was measured against.</summary>
    public decimal? BudgetBlockThresholdUsd { get; private set; }

    /// <summary>What the blocked scope had already spent when the cap was reached.</summary>
    public decimal? BudgetBlockSpentUsd { get; private set; }

    /// <summary>The threads this pass has already acted on, keyed by the comment count that made each due.</summary>
    public ICollection<ThreadPassHandledThread> HandledThreads { get; init; } = new List<ThreadPassHandledThread>();

    /// <summary>Normalized provider host reference for this pass.</summary>
    public ProviderHostRef ProviderHost => new(this.Provider, this.HostBaseUrl ?? this.OrganizationUrl);

    /// <summary>Normalized repository reference for this pass.</summary>
    public RepositoryRef RepositoryReference => new(
        this.ProviderHost,
        this.RepositoryId,
        this.RepositoryOwnerOrNamespace ?? this.ProjectId,
        this.RepositoryProjectPath ?? this.ProjectId);

    /// <summary>Normalized code-review reference for this pass.</summary>
    public CodeReviewRef CodeReviewReference => new(
        this.RepositoryReference,
        this.CodeReviewPlatformKind,
        this.ExternalCodeReviewId ?? this.PullRequestId.ToString(),
        this.PullRequestId);

    /// <summary>Records which connection and model this pass judged its threads with.</summary>
    /// <param name="connectionId">The resolved AI connection, or <see langword="null" /> when none was resolved.</param>
    /// <param name="model">The resolved model identifier, or <see langword="null" /> when none was resolved.</param>
    public void SetAiConfig(Guid? connectionId, string? model)
    {
        this.AiConnectionId = connectionId;
        this.AiModel = model;
    }

    /// <summary>
    ///     Adds what one evaluated thread spent to the pass's running totals.
    /// </summary>
    /// <remarks>
    ///     Additive rather than assigned, because a pass evaluates one thread at a time and each is recorded as it
    ///     completes. An unpriced contribution adds nothing to the total and flags it approximate, so an absent
    ///     price is never read as free.
    /// </remarks>
    /// <param name="inputTokens">Input tokens the evaluation reported.</param>
    /// <param name="outputTokens">Output tokens the evaluation reported.</param>
    /// <param name="costUsd">What the evaluation cost, or <see langword="null" /> when the model has no known price.</param>
    public void AccumulateSpend(long inputTokens, long outputTokens, decimal? costUsd)
    {
        this.TotalInputTokens += inputTokens;
        this.TotalOutputTokens += outputTokens;

        if (costUsd is { } cost)
        {
            this.TotalEstimatedCostUsd = (this.TotalEstimatedCostUsd ?? 0m) + cost;
        }
        else
        {
            this.CostIsApproximate = true;
        }
    }

    /// <summary>Records the cap that stopped this pass, so an operator can see why it did not run.</summary>
    /// <param name="scope">The scope whose cap was reached.</param>
    /// <param name="capKind">Whether the cap was soft or hard.</param>
    /// <param name="thresholdUsd">The configured cap.</param>
    /// <param name="spentUsd">What the scope had spent.</param>
    public void SetBudgetBlock(BudgetScopeKind scope, BudgetCapKind capKind, decimal thresholdUsd, decimal spentUsd)
    {
        this.BudgetBlockScope = scope;
        this.BudgetBlockCapKind = capKind;
        this.BudgetBlockThresholdUsd = thresholdUsd;
        this.BudgetBlockSpentUsd = spentUsd;
    }

    /// <summary>Stores the normalized review target, so provider-neutral writes address the pull request the same way a review job does.</summary>
    /// <param name="codeReview">The normalized review reference for this pull request.</param>
    public void SetProviderReviewContext(CodeReviewRef codeReview)
    {
        ArgumentNullException.ThrowIfNull(codeReview);

        if (codeReview.Number != this.PullRequestId)
        {
            throw new InvalidOperationException("The normalized code review number must match the stored pull request identifier.");
        }

        this.Provider = codeReview.Repository.Host.Provider;
        this.HostBaseUrl = codeReview.Repository.Host.HostBaseUrl;
        this.RepositoryOwnerOrNamespace = codeReview.Repository.OwnerOrNamespace;
        this.RepositoryProjectPath = codeReview.Repository.ProjectPath;
        this.CodeReviewPlatformKind = codeReview.Platform;
        this.ExternalCodeReviewId = codeReview.ExternalReviewId;
    }
}
