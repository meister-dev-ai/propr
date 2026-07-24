// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Represents a request to run a review job for a pull request.
/// </summary>
public sealed class ReviewJob
{
    private readonly List<Guid> _proCursorSourceIds = [];

    private ReviewJob()
    {
        this.OrganizationUrl = string.Empty;
        this.ProjectId = string.Empty;
        this.RepositoryId = string.Empty;
    }

    /// <summary>
    ///     Creates a new <see cref="ReviewJob" />.
    /// </summary>
    public ReviewJob(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(organizationUrl))
        {
            throw new ArgumentException("OrganizationUrl required.", nameof(organizationUrl));
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            throw new ArgumentException("ProjectId required.", nameof(projectId));
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            throw new ArgumentException("RepositoryId required.", nameof(repositoryId));
        }

        if (pullRequestId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestId));
        }

        if (iterationId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(iterationId));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.OrganizationUrl = organizationUrl;
        this.ProjectId = projectId;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.IterationId = iterationId;
        this.Provider = ScmProvider.AzureDevOps;
        this.HostBaseUrl = NormalizeHostBaseUrl(organizationUrl);
        this.RepositoryOwnerOrNamespace = projectId;
        this.RepositoryProjectPath = projectId;
        this.CodeReviewPlatformKind = CodeReviewPlatformKind.PullRequest;
        this.ExternalCodeReviewId = pullRequestId.ToString();
        this.Status = JobStatus.Pending;
        this.SubmittedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     When the job was submitted.
    /// </summary>
    public DateTimeOffset SubmittedAt { get; init; }

    /// <summary>
    ///     When the job completed, if available.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>When the job began processing, if available.</summary>
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>
    ///     Unique identifier for the review job.
    /// </summary>
    public Guid Id { get; init; }

    /// <summary>
    ///     Iteration identifier within the pull request.
    /// </summary>
    public int IterationId { get; init; }

    /// <summary>
    ///     Pull request identifier.
    /// </summary>
    public int PullRequestId { get; init; }

    /// <summary>
    ///     Current status of the job.
    /// </summary>
    public JobStatus Status { get; set; }

    /// <summary>
    ///     Result of the review, if completed. Written only through <see cref="ApplyResult" /> so the
    ///     denormalized <see cref="ResultSummary" /> can never drift from it.
    /// </summary>
    public ReviewResult? Result { get; private set; }

    /// <summary>
    ///     Denormalized copy of <see cref="ValueObjects.ReviewResult.Summary" />, populated when the result is
    ///     finalized via <see cref="ApplyResult" />. Lets the overview list render the summary without
    ///     materializing the full result blob. Null until the review result is finalized.
    /// </summary>
    public string? ResultSummary { get; private set; }

    /// <summary>
    ///     Organization URL containing the repository.
    /// </summary>
    public string OrganizationUrl { get; init; }

    /// <summary>
    ///     Project identifier in the organization.
    /// </summary>
    public string ProjectId { get; init; }

    /// <summary>
    ///     Repository identifier.
    /// </summary>
    public string RepositoryId { get; init; }

    /// <summary>Client that owns this job.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Normalized source-control provider family for this review job.</summary>
    public ScmProvider Provider { get; private set; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host base URL for this review job.</summary>
    public string? HostBaseUrl { get; private set; }

    /// <summary>Provider-neutral repository owner, namespace, or project scope.</summary>
    public string? RepositoryOwnerOrNamespace { get; private set; }

    /// <summary>Provider-neutral repository project path.</summary>
    public string? RepositoryProjectPath { get; private set; }

    /// <summary>Native code-review platform kind for this review job.</summary>
    public CodeReviewPlatformKind CodeReviewPlatformKind { get; private set; } = CodeReviewPlatformKind.PullRequest;

    /// <summary>Provider-native external review identifier.</summary>
    public string? ExternalCodeReviewId { get; private set; }

    /// <summary>Normalized review revision head SHA when available.</summary>
    public string? RevisionHeadSha { get; private set; }

    /// <summary>Normalized review revision base SHA when available.</summary>
    public string? RevisionBaseSha { get; private set; }

    /// <summary>Normalized review revision start SHA when available.</summary>
    public string? RevisionStartSha { get; private set; }

    /// <summary>Provider-native revision identifier when available.</summary>
    public string? ProviderRevisionId { get; private set; }

    /// <summary>Patch identity used for deduplication when available.</summary>
    public string? ReviewPatchIdentity { get; private set; }

    /// <summary>Normalized provider host reference for this job.</summary>
    public ProviderHostRef ProviderHost => new(this.Provider, this.HostBaseUrl ?? this.OrganizationUrl);

    /// <summary>Normalized repository reference for this job.</summary>
    public RepositoryRef RepositoryReference => new(
        this.ProviderHost,
        this.RepositoryId,
        this.RepositoryOwnerOrNamespace ?? this.ProjectId,
        this.RepositoryProjectPath ?? this.ProjectId);

    /// <summary>Normalized code-review reference for this job.</summary>
    public CodeReviewRef CodeReviewReference => new(
        this.RepositoryReference,
        this.CodeReviewPlatformKind,
        this.ExternalCodeReviewId ?? this.PullRequestId.ToString(),
        this.PullRequestId);

    /// <summary>Normalized review revision for this job when available.</summary>
    public ReviewRevision? ReviewRevisionReference => string.IsNullOrWhiteSpace(this.RevisionHeadSha) ||
                                                      string.IsNullOrWhiteSpace(this.RevisionBaseSha)
        ? null
        : new ReviewRevision(
            this.RevisionHeadSha,
            this.RevisionBaseSha,
            this.RevisionStartSha,
            this.ProviderRevisionId,
            this.ReviewPatchIdentity);

    /// <summary>
    ///     Error message if the job failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Number of retries performed for this job.
    /// </summary>
    public int RetryCount { get; set; } = 0;

    /// <summary>
    ///     The protocol records for this job's execution attempts.
    ///     Populated only when the job has been loaded via <c>IJobRepository.GetByIdWithProtocolsAsync</c>.
    /// </summary>
    public ICollection<ReviewJobProtocol> Protocols { get; } = [];

    /// <summary>
    ///     The per-file results of this review job.
    ///     Populated only when the job has been loaded via <c>IJobRepository.GetByIdWithFileResultsAsync</c>.
    /// </summary>
    public ICollection<ReviewFileResult> FileReviewResults { get; } = [];

    /// <summary>Optional internal pipeline profile selection captured at intake time.</summary>
    public string? ReviewPipelineProfileId { get; private set; }

    /// <summary>
    ///     Snapshotted ProCursor source-scope mode captured when this job was queued.
    /// </summary>
    public ProCursorSourceScopeMode ProCursorSourceScopeMode { get; private set; } =
        ProCursorSourceScopeMode.AllClientSources;

    /// <summary>
    ///     Snapshotted ProCursor source IDs captured when this job was queued.
    ///     Empty when the job uses the full client-wide source set.
    /// </summary>
    public IReadOnlyList<Guid> ProCursorSourceIds => this._proCursorSourceIds.AsReadOnly();

    /// <summary>Running aggregate of input tokens across all protocol passes.</summary>
    public long? TotalInputTokensAggregated { get; private set; }

    /// <summary>Running aggregate of output tokens across all protocol passes.</summary>
    public long? TotalOutputTokensAggregated { get; private set; }

    /// <summary>Running aggregate of cache-read input tokens across all protocol passes.</summary>
    public long? TotalCachedInputTokensAggregated { get; private set; }

    /// <summary>Running aggregate of cache-write tokens across all protocol passes.</summary>
    public long? TotalCacheWriteTokensAggregated { get; private set; }

    /// <summary>Running aggregate of reasoning tokens across all protocol passes.</summary>
    public long? TotalReasoningTokensAggregated { get; private set; }

    /// <summary>
    ///     Sum of the per-tier <see cref="TokenBreakdownEntry.EstimatedCostUsd" /> values across all priced
    ///     tiers, in USD. Null when no tier has configured pricing (distinct from a real cost of zero).
    /// </summary>
    public decimal? TotalEstimatedCostUsd { get; private set; }

    /// <summary>
    ///     True when the job cost rests on a fallback rate, a missing rate, estimated token counts, or a mix
    ///     of priced and unpriced tiers.
    /// </summary>
    public bool CostIsApproximate { get; private set; }

    /// <summary>The budget scope whose cap held or stopped this job, or null when no budget blocked it.</summary>
    public BudgetScopeKind? BudgetBlockScope { get; private set; }

    /// <summary>Whether the soft or the hard cap held or stopped this job. Null when no budget blocked it.</summary>
    public BudgetCapKind? BudgetBlockCapKind { get; private set; }

    /// <summary>The USD threshold that held or stopped this job. Null when no budget blocked it.</summary>
    public decimal? BudgetBlockThresholdUsd { get; private set; }

    /// <summary>The scope spend that reached the threshold when this job was held or stopped. Null when no budget blocked it.</summary>
    public decimal? BudgetBlockSpentUsd { get; private set; }

    /// <summary>
    ///     The number of in-scope changed files after exclusions for this iteration, fixed once at dispatch
    ///     planning. Null until dispatch planning runs. Denominator of the "files reviewed" progress metric.
    /// </summary>
    public int? InScopeChangedFileCount { get; private set; }

    /// <summary>
    ///     Per-tier token cost breakdown. Serialised as JSONB.
    ///     Each entry represents one (effort tier, model ID) combination observed across all protocols in this job.
    /// </summary>
    public List<TokenBreakdownEntry> TokenBreakdown { get; } = [];

    /// <summary>Snapshot of the AI connection ID used when the job started. Nullable for backward compat.</summary>
    public Guid? AiConnectionId { get; private set; }

    /// <summary>Snapshot of the AI model deployment used when the job started. Nullable for backward compat.</summary>
    public string? AiModel { get; private set; }

    /// <summary>Snapshot of the review temperature used by this job. Nullable when default model behavior is used.</summary>
    public float? ReviewTemperature { get; private set; }

    /// <summary>PR display title captured from ADO at job-creation time. Null when unavailable.</summary>
    public string? PrTitle { get; private set; }

    /// <summary>Source branch display name (refs/heads/ stripped). Null when unavailable.</summary>
    public string? PrSourceBranch { get; private set; }

    /// <summary>Target branch display name (refs/heads/ stripped). Null when unavailable.</summary>
    public string? PrTargetBranch { get; private set; }

    /// <summary>Repository display name from ADO. Null when unavailable.</summary>
    public string? PrRepositoryName { get; private set; }

    /// <summary>
    ///     Finalizes the review result onto the job and denormalizes its summary into
    ///     <see cref="ResultSummary" />. The single seam both repository implementations use, so the stored
    ///     summary stays consistent across first runs, restarts, and resumes (all re-finalize through here).
    /// </summary>
    /// <param name="result">The completed review result.</param>
    public void ApplyResult(ReviewResult result)
    {
        this.Result = result;
        this.ResultSummary = result.Summary;
    }

    /// <summary>Increments the token aggregates. Called after each protocol pass completes.</summary>
    public void AccumulateTokens(
        long inputTokens,
        long outputTokens,
        long cachedInputTokens = 0,
        long cacheWriteTokens = 0,
        long reasoningTokens = 0)
    {
        this.TotalInputTokensAggregated = (this.TotalInputTokensAggregated ?? 0) + inputTokens;
        this.TotalOutputTokensAggregated = (this.TotalOutputTokensAggregated ?? 0) + outputTokens;
        this.TotalCachedInputTokensAggregated = (this.TotalCachedInputTokensAggregated ?? 0) + cachedInputTokens;
        this.TotalCacheWriteTokensAggregated = (this.TotalCacheWriteTokensAggregated ?? 0) + cacheWriteTokens;
        this.TotalReasoningTokensAggregated = (this.TotalReasoningTokensAggregated ?? 0) + reasoningTokens;
    }

    /// <summary>
    ///     Merges tokens into the per-tier breakdown and increments the flat aggregates.
    ///     If an entry for the (category, modelId) pair already exists it is updated in-place;
    ///     otherwise a new entry is appended. Then calls <see cref="AccumulateTokens" />.
    /// </summary>
    public void AccumulateTierTokens(
        AiConnectionModelCategory category,
        string modelId,
        long inputTokens,
        long outputTokens,
        long cachedInputTokens = 0,
        long cacheWriteTokens = 0,
        long reasoningTokens = 0,
        string? logicalModelName = null)
    {
        var existing = this.TokenBreakdown.Find(e =>
            e.ConnectionCategory == category &&
            string.Equals(e.ModelId, modelId, StringComparison.Ordinal) &&
            string.Equals(e.LogicalModelName, logicalModelName, StringComparison.Ordinal));

        if (existing is not null)
        {
            this.TokenBreakdown.Remove(existing);
            this.TokenBreakdown.Add(
                existing with
                {
                    TotalInputTokens = existing.TotalInputTokens + inputTokens,
                    TotalOutputTokens = existing.TotalOutputTokens + outputTokens,
                    TotalCachedInputTokens = existing.TotalCachedInputTokens + cachedInputTokens,
                    TotalCacheWriteTokens = existing.TotalCacheWriteTokens + cacheWriteTokens,
                    TotalReasoningTokens = existing.TotalReasoningTokens + reasoningTokens,
                });
        }
        else
        {
            this.TokenBreakdown.Add(
                new TokenBreakdownEntry(
                    category,
                    modelId,
                    inputTokens,
                    outputTokens,
                    cachedInputTokens,
                    cacheWriteTokens,
                    reasoningTokens,
                    LogicalModelName: logicalModelName));
        }

        this.AccumulateTokens(inputTokens, outputTokens, cachedInputTokens, cacheWriteTokens, reasoningTokens);
    }

    /// <summary>
    ///     Records the estimated USD cost for the tier/model breakdown entry matching
    ///     (<paramref name="category" />, <paramref name="modelId" />), then recomputes the null-aware job
    ///     total and approximation flag. Idempotent: the cost is derived from the entry's cumulative token
    ///     totals, so repeated calls after top-up merges converge on the same value. No-op when no matching
    ///     entry exists yet.
    /// </summary>
    /// <param name="category">The AI connection category (effort tier) whose entry to update.</param>
    /// <param name="modelId">The effective model deployment name whose entry to update.</param>
    /// <param name="estimatedCostUsd">The estimated USD cost for the entry, or <see langword="null" /> when the model has no configured pricing.</param>
    /// <param name="costIsApproximate">True when the estimate rests on a fallback rate, a missing rate, or estimated token counts.</param>
    public void SetTierCost(
        AiConnectionModelCategory category,
        string modelId,
        decimal? estimatedCostUsd,
        bool costIsApproximate,
        string? logicalModelName = null)
    {
        var index = this.TokenBreakdown.FindIndex(e =>
            e.ConnectionCategory == category &&
            string.Equals(e.ModelId, modelId, StringComparison.Ordinal) &&
            string.Equals(e.LogicalModelName, logicalModelName, StringComparison.Ordinal));

        if (index >= 0)
        {
            this.TokenBreakdown[index] = this.TokenBreakdown[index] with
            {
                EstimatedCostUsd = estimatedCostUsd,
                CostIsApproximate = costIsApproximate,
            };
        }

        this.RecomputeCost();
    }

    /// <summary>
    ///     Recomputes <see cref="TotalEstimatedCostUsd" /> as the null-aware sum of the per-tier costs (null
    ///     when every tier is unpriced) and <see cref="CostIsApproximate" /> as true when any tier is
    ///     approximate or the job mixes priced and unpriced tiers.
    /// </summary>
    private void RecomputeCost()
    {
        decimal? total = null;
        var anyApproximate = false;
        var anyPriced = false;
        var anyUnpriced = false;

        foreach (var entry in this.TokenBreakdown)
        {
            if (entry.EstimatedCostUsd is { } cost)
            {
                total = (total ?? 0m) + cost;
                anyPriced = true;
            }
            else
            {
                anyUnpriced = true;
            }

            if (entry.CostIsApproximate)
            {
                anyApproximate = true;
            }
        }

        this.TotalEstimatedCostUsd = total;
        this.CostIsApproximate = anyApproximate || (anyPriced && anyUnpriced);
    }

    /// <summary>
    ///     Records why a budget held or stopped this job: the binding scope, whether the soft or hard cap was
    ///     reached, the USD threshold, and the scope spend that reached it. Surfaced to operators as the reason.
    /// </summary>
    public void SetBudgetBlock(BudgetScopeKind scope, BudgetCapKind capKind, decimal thresholdUsd, decimal spentUsd)
    {
        this.BudgetBlockScope = scope;
        this.BudgetBlockCapKind = capKind;
        this.BudgetBlockThresholdUsd = thresholdUsd;
        this.BudgetBlockSpentUsd = spentUsd;
    }

    /// <summary>Records the AI connection and model used at job-start time.</summary>
    public void SetAiConfig(Guid? connectionId, string? model, float? reviewTemperature = null)
    {
        this.AiConnectionId = connectionId;
        this.AiModel = model;
        this.ReviewTemperature = reviewTemperature;
    }

    /// <summary>Stores the internal pipeline profile snapshot selected at intake time.</summary>
    public void SetReviewPipelineProfile(string? pipelineProfileId)
    {
        this.ReviewPipelineProfileId = string.IsNullOrWhiteSpace(pipelineProfileId) ? null : pipelineProfileId;
    }

    /// <summary>
    ///     Records the count of in-scope changed files after exclusions, computed once at dispatch planning.
    ///     Deterministic per job/iteration, so re-running dispatch on a retry recomputes the same value.
    /// </summary>
    /// <param name="count">The in-scope changed-file count; negative inputs are clamped to zero.</param>
    public void SetInScopeChangedFileCount(int count)
    {
        this.InScopeChangedFileCount = count < 0 ? 0 : count;
    }

    /// <summary>Records the PR context snapshot captured from ADO at job-creation time.</summary>
    public void SetPrContext(string? title, string? repositoryName, string? sourceBranch, string? targetBranch)
    {
        this.PrTitle = title;
        this.PrRepositoryName = repositoryName;
        this.PrSourceBranch = StripRefsHeads(sourceBranch);
        this.PrTargetBranch = StripRefsHeads(targetBranch);
    }

    /// <summary>Stores the normalized review target for this job while preserving legacy ADO compatibility fields.</summary>
    public void SetProviderReviewContext(CodeReviewRef codeReview)
    {
        ArgumentNullException.ThrowIfNull(codeReview);

        if (!string.Equals(codeReview.Repository.ExternalRepositoryId, this.RepositoryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The normalized repository reference must match the stored repository identifier.");
        }

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

    /// <summary>Stores the normalized review revision for this job.</summary>
    public void SetReviewRevision(ReviewRevision? reviewRevision)
    {
        this.RevisionHeadSha = reviewRevision?.HeadSha;
        this.RevisionBaseSha = reviewRevision?.BaseSha;
        this.RevisionStartSha = reviewRevision?.StartSha;
        this.ProviderRevisionId = reviewRevision?.ProviderRevisionId;
        this.ReviewPatchIdentity = reviewRevision?.PatchIdentity;
    }

    /// <summary>Records the ProCursor source scope snapshotted when the job was queued.</summary>
    public void SetProCursorSourceScope(ProCursorSourceScopeMode scopeMode, IReadOnlyList<Guid>? sourceIds)
    {
        this.ProCursorSourceScopeMode = scopeMode;
        this._proCursorSourceIds.Clear();

        if (scopeMode != ProCursorSourceScopeMode.SelectedSources)
        {
            return;
        }

        foreach (var sourceId in sourceIds ?? [])
        {
            if (sourceId == Guid.Empty || this._proCursorSourceIds.Contains(sourceId))
            {
                continue;
            }

            this._proCursorSourceIds.Add(sourceId);
        }
    }

    private static string? StripRefsHeads(string? branch)
    {
        return branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? branch[11..] : branch;
    }

    private static string NormalizeHostBaseUrl(string organizationUrl)
    {
        return new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl).HostBaseUrl;
    }
}
