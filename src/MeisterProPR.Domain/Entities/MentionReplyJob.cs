// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Represents a pending reply job triggered by a pull request comment mention.
///     State machine: <see cref="MentionJobStatus.Pending" /> →
///     <see cref="MentionJobStatus.Processing" /> →
///     <see cref="MentionJobStatus.Completed" /> | <see cref="MentionJobStatus.Failed" />.
/// </summary>
public sealed class MentionReplyJob
{
    private MentionReplyJob()
    {
        this.OrganizationUrl = string.Empty;
        this.ProjectId = string.Empty;
        this.RepositoryId = string.Empty;
        this.MentionText = string.Empty;
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
        int threadId,
        int commentId,
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
        ArgumentOutOfRangeException.ThrowIfLessThan(threadId, 1);
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
        this.ThreadId.ToString(),
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

    /// <summary>ADO thread ID containing the mention.</summary>
    public int ThreadId { get; init; }

    /// <summary>ADO comment ID of the mention comment.</summary>
    public int CommentId { get; init; }

    /// <summary>Raw content of the mention comment.</summary>
    public string MentionText { get; init; }

    /// <summary>Current status of the job.</summary>
    public MentionJobStatus Status { get; set; }

    /// <summary>When the job was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When processing began, if available.</summary>
    public DateTimeOffset? ProcessingStartedAt { get; set; }

    /// <summary>When the job finished (success or failure), if available.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Error details if the job failed.</summary>
    public string? ErrorMessage { get; set; }

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
