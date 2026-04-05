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
    ///     Result of the review, if completed.
    /// </summary>
    public ReviewResult? Result { get; set; }

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

    /// <summary>
    ///     Snapshotted ProCursor source-scope mode captured when this job was queued.
    /// </summary>
    public ProCursorSourceScopeMode ProCursorSourceScopeMode { get; private set; } = ProCursorSourceScopeMode.AllClientSources;

    /// <summary>
    ///     Snapshotted ProCursor source IDs captured when this job was queued.
    ///     Empty when the job uses the full client-wide source set.
    /// </summary>
    public IReadOnlyList<Guid> ProCursorSourceIds => this._proCursorSourceIds.AsReadOnly();

    /// <summary>Running aggregate of input tokens across all protocol passes.</summary>
    public long? TotalInputTokensAggregated { get; private set; }

    /// <summary>Running aggregate of output tokens across all protocol passes.</summary>
    public long? TotalOutputTokensAggregated { get; private set; }

    /// <summary>
    ///     Per-tier token cost breakdown. Serialised as JSONB.
    ///     Each entry represents one (effort tier, model ID) combination observed across all protocols in this job.
    /// </summary>
    public List<TokenBreakdownEntry> TokenBreakdown { get; private set; } = [];

    /// <summary>Snapshot of the AI connection ID used when the job started. Nullable for backward compat.</summary>
    public Guid? AiConnectionId { get; private set; }

    /// <summary>Snapshot of the AI model deployment used when the job started. Nullable for backward compat.</summary>
    public string? AiModel { get; private set; }

    /// <summary>PR display title captured from ADO at job-creation time. Null when unavailable.</summary>
    public string? PrTitle { get; private set; }

    /// <summary>Source branch display name (refs/heads/ stripped). Null when unavailable.</summary>
    public string? PrSourceBranch { get; private set; }

    /// <summary>Target branch display name (refs/heads/ stripped). Null when unavailable.</summary>
    public string? PrTargetBranch { get; private set; }

    /// <summary>Repository display name from ADO. Null when unavailable.</summary>
    public string? PrRepositoryName { get; private set; }

    /// <summary>Increments the token aggregates. Called after each protocol pass completes.</summary>
    public void AccumulateTokens(long inputTokens, long outputTokens)
    {
        this.TotalInputTokensAggregated = (this.TotalInputTokensAggregated ?? 0) + inputTokens;
        this.TotalOutputTokensAggregated = (this.TotalOutputTokensAggregated ?? 0) + outputTokens;
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
        long outputTokens)
    {
        var existing = this.TokenBreakdown.Find(e =>
            e.ConnectionCategory == category &&
            string.Equals(e.ModelId, modelId, StringComparison.Ordinal));

        if (existing is not null)
        {
            this.TokenBreakdown.Remove(existing);
            this.TokenBreakdown.Add(existing with
            {
                TotalInputTokens = existing.TotalInputTokens + inputTokens,
                TotalOutputTokens = existing.TotalOutputTokens + outputTokens,
            });
        }
        else
        {
            this.TokenBreakdown.Add(new TokenBreakdownEntry(category, modelId, inputTokens, outputTokens));
        }

        this.AccumulateTokens(inputTokens, outputTokens);
    }

    /// <summary>Records the AI connection and model used at job-start time.</summary>
    public void SetAiConfig(Guid? connectionId, string? model)
    {
        this.AiConnectionId = connectionId;
        this.AiModel = model;
    }

    /// <summary>Records the PR context snapshot captured from ADO at job-creation time.</summary>
    public void SetPrContext(string? title, string? repositoryName, string? sourceBranch, string? targetBranch)
    {
        this.PrTitle = title;
        this.PrRepositoryName = repositoryName;
        this.PrSourceBranch = StripRefsHeads(sourceBranch);
        this.PrTargetBranch = StripRefsHeads(targetBranch);
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

    private static string? StripRefsHeads(string? branch) =>
        branch?.StartsWith("refs/heads/", StringComparison.Ordinal) == true ? branch[11..] : branch;
}
