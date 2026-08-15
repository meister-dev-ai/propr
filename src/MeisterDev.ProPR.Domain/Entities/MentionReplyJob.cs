// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     Represents a pending reply job triggered by a pull request comment mention.
///     State machine: <see cref="MentionJobStatus.Pending" /> →
///     <see cref="MentionJobStatus.Processing" /> →
///     <see cref="MentionJobStatus.Completed" /> | <see cref="MentionJobStatus.Failed" /> |
///     <see cref="MentionJobStatus.BudgetHeld" />.
/// </summary>
/// <remarks>
///     All three end states are terminal, and the row is never deleted, so the duplicate guard on the mention
///     comment keeps the same question from being answered twice. That is what stops a budget-refused answer
///     from posting its note again on every scan.
/// </remarks>
public sealed class MentionReplyJob
{
    private MentionReplyJob()
    {
        this.OrganizationUrl = string.Empty;
        this.ProjectId = string.Empty;
        this.RepositoryId = string.Empty;
        this.MentionText = string.Empty;
        this.ThreadId = string.Empty;
    }

    /// <summary>
    ///     Creates a new <see cref="MentionReplyJob" />.
    /// </summary>
    public MentionReplyJob(
        Guid id,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string threadId,
        long commentId,
        string mentionText,
        string? threadFilePath = null,
        int? threadLineNumber = null,
        Guid? commentAuthorId = null,
        string? commentAuthorName = null,
        DateTimeOffset? commentPublishedAt = null)
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
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentOutOfRangeException.ThrowIfLessThan(commentId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(mentionText);

        this.Id = id;
        this.ClientId = clientId;
        this.OrganizationUrl = organizationUrl;
        this.ProjectId = projectId;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.ThreadId = threadId;
        this.CommentId = commentId;
        this.MentionText = mentionText;
        this.Provider = ScmProvider.AzureDevOps;
        this.HostBaseUrl = NormalizeHostBaseUrl(organizationUrl);
        this.RepositoryOwnerOrNamespace = projectId;
        this.RepositoryProjectPath = projectId;
        this.CodeReviewPlatformKind = CodeReviewPlatformKind.PullRequest;
        this.ExternalCodeReviewId = pullRequestId.ToString();
        this.ThreadFilePath = NormalizeOptional(threadFilePath);
        this.ThreadLineNumber = threadLineNumber;
        this.CommentAuthorExternalUserId = commentAuthorId?.ToString("D");
        this.CommentAuthorLogin = NormalizeOptional(commentAuthorName);
        this.CommentAuthorDisplayName = NormalizeOptional(commentAuthorName);
        this.CommentPublishedAt = commentPublishedAt;
        this.Status = MentionJobStatus.Pending;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier for the mention reply job.</summary>
    public Guid Id { get; init; }

    /// <summary>Client that owns this job.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Normalized source-control provider family for this mention reply job.</summary>
    public ScmProvider Provider { get; private set; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host base URL for this mention reply job.</summary>
    public string? HostBaseUrl { get; private set; }

    /// <summary>Provider-neutral repository owner, namespace, or project scope.</summary>
    public string? RepositoryOwnerOrNamespace { get; private set; }

    /// <summary>Provider-neutral repository project path.</summary>
    public string? RepositoryProjectPath { get; private set; }

    /// <summary>Native code-review platform kind for this mention reply job.</summary>
    public CodeReviewPlatformKind CodeReviewPlatformKind { get; private set; } = CodeReviewPlatformKind.PullRequest;

    /// <summary>Provider-native external review identifier.</summary>
    public string? ExternalCodeReviewId { get; private set; }

    /// <summary>Optional file path anchor for the referenced thread.</summary>
    public string? ThreadFilePath { get; private set; }

    /// <summary>Optional line anchor for the referenced thread.</summary>
    public int? ThreadLineNumber { get; private set; }

    /// <summary>Provider-native external user identifier for the mention comment author when known.</summary>
    public string? CommentAuthorExternalUserId { get; private set; }

    /// <summary>Normalized login for the mention comment author when known.</summary>
    public string? CommentAuthorLogin { get; private set; }

    /// <summary>Display name for the mention comment author when known.</summary>
    public string? CommentAuthorDisplayName { get; private set; }

    /// <summary>Whether the captured mention comment author is a bot.</summary>
    public bool CommentAuthorIsBot { get; private set; }

    /// <summary>Published timestamp of the mention comment when known.</summary>
    public DateTimeOffset? CommentPublishedAt { get; private set; }

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

    /// <summary>Normalized review thread reference for this job.</summary>
    public ReviewThreadRef ReviewThreadReference => new(
        this.CodeReviewReference,
        this.ThreadId,
        this.ThreadFilePath,
        this.ThreadLineNumber,
        false);

    /// <summary>Normalized review comment reference for this job when the author is known.</summary>
    public ReviewCommentRef? ReviewCommentReference => string.IsNullOrWhiteSpace(this.CommentAuthorExternalUserId) ||
                                                       string.IsNullOrWhiteSpace(this.CommentAuthorLogin)
        ? null
        : new ReviewCommentRef(
            this.ReviewThreadReference,
            this.CommentId.ToString(),
            new ReviewerIdentity(
                this.ProviderHost,
                this.CommentAuthorExternalUserId,
                this.CommentAuthorLogin,
                this.CommentAuthorDisplayName ?? this.CommentAuthorLogin,
                this.CommentAuthorIsBot),
            this.CommentPublishedAt);

    /// <summary>ADO organization URL.</summary>
    public string OrganizationUrl { get; init; }

    /// <summary>ADO project identifier.</summary>
    public string ProjectId { get; init; }

    /// <summary>ADO repository identifier.</summary>
    public string RepositoryId { get; init; }

    /// <summary>ADO pull request number.</summary>
    public int PullRequestId { get; init; }

    /// <summary>Provider-native identifier of the thread containing the mention.</summary>
    public string ThreadId { get; init; }

    /// <summary>ADO comment ID of the mention comment.</summary>
    public long CommentId { get; init; }

    /// <summary>Raw content of the mention comment.</summary>
    public string MentionText { get; init; }

    /// <summary>
    ///     Stable key for the reviewer account this mention addressed, from
    ///     <see cref="ReviewerIdentity.AddressedKey" />.
    /// </summary>
    /// <remarks>
    ///     Part of the uniqueness rule that keeps one question to one answer. The account that was addressed
    ///     is a property of the comment rather than of any client, so two clients that both cover the
    ///     repository and resolve the same reviewer identity describe the same unit of work and only one of
    ///     them may create it. The account that eventually writes the reply is a different one, the
    ///     authenticated connection identity, and it is not what this records.
    /// </remarks>
    public string MentionedReviewerKey { get; private set; } = string.Empty;

    /// <summary>Records which reviewer account the mention addressed.</summary>
    /// <param name="reviewer">The reviewer identity the mention detector matched.</param>
    public void SetMentionedReviewer(ReviewerIdentity reviewer)
    {
        ArgumentNullException.ThrowIfNull(reviewer);
        this.MentionedReviewerKey = reviewer.AddressedKey;
    }

    /// <summary>Current status of the job.</summary>
    public MentionJobStatus Status { get; set; }

    /// <summary>When the job was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When processing began, if available.</summary>
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>When the job finished (success or failure), if available.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    ///     Provider-native identifier of the reply comment this job posted, recorded by the same update that
    ///     completes the job. Null while the job has not posted, and for an adapter that could not report one.
    /// </summary>
    /// <remarks>
    ///     The answer's provenance has to be reconstructable from persisted state alone. The comment id is only
    ///     known in the posting process, and a crash between completing the job and writing the provenance row
    ///     used to lose it for good: the reply stays on the pull request, the job is complete, and nothing knows
    ///     an origin row is missing. Carrying the id on the job means the missing row is derivable afterwards,
    ///     and it costs no extra write, because it rides along on the completion update.
    /// </remarks>
    public string? PostedReplyCommentId { get; set; }

    /// <summary>Error details if the job failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     The increment this answer was charged to, or <see langword="null" /> when the provider could not
    ///     say which one was current.
    /// </summary>
    /// <remarks>
    ///     A mention is not raised against a revision the way a review or a thread pass is: it arrives whenever
    ///     someone asks, and the scan reads the pull request at a fixed iteration. The increment is therefore
    ///     whichever one was current when the answer was written, resolved at that moment. A lookup that fails
    ///     leaves this null rather than guessing, and the increment budget scope then reads this row against the
    ///     whole pull request, which counts it too widely rather than not at all.
    /// </remarks>
    public int? IterationId { get; private set; }

    /// <summary>The AI connection whose model answered, recorded when the runtime was resolved.</summary>
    public Guid? AiConnectionId { get; private set; }

    /// <summary>The model that answered, recorded when the runtime was resolved.</summary>
    public string? AiModel { get; private set; }

    /// <summary>Input tokens this answer spent.</summary>
    public long TotalInputTokens { get; private set; }

    /// <summary>Output tokens this answer spent.</summary>
    public long TotalOutputTokens { get; private set; }

    /// <summary>
    ///     Estimated USD cost of what this answer spent, or <see langword="null" /> while nothing priced has
    ///     been recorded. The per-pull-request and per-increment budget scopes read this column.
    /// </summary>
    public decimal? TotalEstimatedCostUsd { get; private set; }

    /// <summary>True when some recorded spend had no known price, so the total is a lower bound.</summary>
    public bool CostIsApproximate { get; private set; }

    /// <summary>The budget scope that stopped this answer, if one did.</summary>
    public BudgetScopeKind? BudgetBlockScope { get; private set; }

    /// <summary>Whether the cap that stopped this answer was a soft or a hard one.</summary>
    public BudgetCapKind? BudgetBlockCapKind { get; private set; }

    /// <summary>The cap the blocked scope was measured against.</summary>
    public decimal? BudgetBlockThresholdUsd { get; private set; }

    /// <summary>What the blocked scope had already spent when the cap was reached.</summary>
    public decimal? BudgetBlockSpentUsd { get; private set; }

    /// <summary>Records the increment this answer is charged to.</summary>
    /// <param name="iterationId">The increment current when the answer was written, or null when unknown.</param>
    public void SetIteration(int? iterationId)
    {
        this.IterationId = iterationId;
    }

    /// <summary>Records which connection and model produced the answer.</summary>
    /// <param name="connectionId">The resolved AI connection, or <see langword="null" /> when none was resolved.</param>
    /// <param name="model">The resolved model identifier, or <see langword="null" /> when none was resolved.</param>
    public void SetAiConfig(Guid? connectionId, string? model)
    {
        this.AiConnectionId = connectionId;
        this.AiModel = model;
    }

    /// <summary>Adds what one model call spent to this answer's totals.</summary>
    /// <remarks>
    ///     Additive rather than assigned, so an answer that grows a second model call sums the two instead of
    ///     reporting only the last. Additive is not idempotent: recording the same call twice counts it twice,
    ///     so the caller must close a given trace record once. An unpriced contribution adds nothing to the
    ///     total and flags it approximate, so an absent price is never read as free.
    /// </remarks>
    /// <param name="inputTokens">Input tokens the call reported.</param>
    /// <param name="outputTokens">Output tokens the call reported.</param>
    /// <param name="costUsd">What the call cost, or <see langword="null" /> when the model has no known price.</param>
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

    /// <summary>Records the cap that stopped this answer, so an operator can see why it was not written.</summary>
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

    /// <summary>Stores the normalized review target for this mention job while preserving legacy compatibility fields.</summary>
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

    /// <summary>Stores the normalized thread anchor for this mention job.</summary>
    public void SetReviewThreadContext(ReviewThreadRef thread)
    {
        ArgumentNullException.ThrowIfNull(thread);

        this.SetProviderReviewContext(thread.Review);
        this.ThreadFilePath = NormalizeOptional(thread.FilePath);
        this.ThreadLineNumber = thread.LineNumber;
    }

    /// <summary>Stores the normalized comment author and publication details for this mention job.</summary>
    public void SetReviewCommentContext(ReviewCommentRef comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        this.SetReviewThreadContext(comment.Thread);
        this.CommentAuthorExternalUserId = comment.Author.ExternalUserId;
        this.CommentAuthorLogin = NormalizeOptional(comment.Author.Login);
        this.CommentAuthorDisplayName = NormalizeOptional(comment.Author.DisplayName);
        this.CommentAuthorIsBot = comment.Author.IsBot;
        this.CommentPublishedAt = comment.PublishedAt;
    }

    private static string NormalizeHostBaseUrl(string organizationUrl)
    {
        return new ProviderHostRef(ScmProvider.AzureDevOps, organizationUrl).HostBaseUrl;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
