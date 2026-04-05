// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Represents a pending reply job triggered by a pull request comment mention.
///     State machine: <see cref="MentionJobStatus.Pending" /> →
///     <see cref="MentionJobStatus.Processing" /> →
///     <see cref="MentionJobStatus.Completed" /> | <see cref="MentionJobStatus.Failed" />.
/// </summary>
public sealed class MentionReplyJob
{
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
        string mentionText)
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
        this.Status = MentionJobStatus.Pending;
        this.CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier for the mention reply job.</summary>
    public Guid Id { get; init; }

    /// <summary>Client that owns this job.</summary>
    public Guid ClientId { get; init; }

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
}
