// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Durable queue record for full or incremental ProCursor indexing work.
/// </summary>
public sealed class ProCursorIndexJob
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorIndexJob" /> class.
    /// </summary>
    /// <param name="id">The unique identifier for the job.</param>
    /// <param name="knowledgeSourceId">The knowledge source identifier.</param>
    /// <param name="trackedBranchId">The tracked branch identifier.</param>
    /// <param name="requestedCommitSha">The requested commit SHA, if any.</param>
    /// <param name="jobKind">The kind of job to perform.</param>
    /// <param name="dedupKey">The deduplication key for the job.</param>
    public ProCursorIndexJob(
        Guid id,
        Guid knowledgeSourceId,
        Guid trackedBranchId,
        string? requestedCommitSha,
        string jobKind,
        string dedupKey)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (knowledgeSourceId == Guid.Empty)
        {
            throw new ArgumentException("KnowledgeSourceId must not be empty.", nameof(knowledgeSourceId));
        }

        if (trackedBranchId == Guid.Empty)
        {
            throw new ArgumentException("TrackedBranchId must not be empty.", nameof(trackedBranchId));
        }

        this.Id = id;
        this.KnowledgeSourceId = knowledgeSourceId;
        this.TrackedBranchId = trackedBranchId;
        this.RequestedCommitSha = NormalizeOptional(requestedCommitSha);
        this.JobKind = NormalizeRequired(jobKind, nameof(jobKind));
        this.Status = ProCursorIndexJobStatus.Pending;
        this.DedupKey = NormalizeRequired(dedupKey, nameof(dedupKey));
        this.QueuedAt = DateTimeOffset.UtcNow;
    }

    private ProCursorIndexJob()
    {
    }

    /// <summary>
    ///     Gets the unique identifier for the job.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the knowledge source identifier.
    /// </summary>
    public Guid KnowledgeSourceId { get; private set; }

    /// <summary>
    ///     Gets the tracked branch identifier.
    /// </summary>
    public Guid TrackedBranchId { get; private set; }

    /// <summary>
    ///     Gets the requested commit SHA, if any.
    /// </summary>
    public string? RequestedCommitSha { get; private set; }

    /// <summary>
    ///     Gets the kind of job to perform.
    /// </summary>
    public string JobKind { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the current status of the job.
    /// </summary>
    public ProCursorIndexJobStatus Status { get; private set; }

    /// <summary>
    ///     Gets the deduplication key for the job.
    /// </summary>
    public string DedupKey { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the number of times this job has been attempted.
    /// </summary>
    public int AttemptCount { get; private set; }

    /// <summary>
    ///     Gets the timestamp when the job was queued.
    /// </summary>
    public DateTimeOffset QueuedAt { get; private set; }

    /// <summary>
    ///     Gets the timestamp when the job was started processing, if any.
    /// </summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>
    ///     Gets the timestamp when the job completed, if any.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    ///     Gets the reason for failure, if any.
    /// </summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    ///     Marks the job as processing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the job is not in pending status.</exception>
    public void MarkProcessing()
    {
        if (this.Status != ProCursorIndexJobStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot start a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Processing;
        this.AttemptCount++;
        this.StartedAt = DateTimeOffset.UtcNow;
        this.CompletedAt = null;
        this.FailureReason = null;
    }

    /// <summary>
    ///     Marks the job as completed.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the job is not in processing status.</exception>
    public void MarkCompleted()
    {
        if (this.Status != ProCursorIndexJobStatus.Processing)
        {
            throw new InvalidOperationException($"Cannot complete a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Completed;
        this.CompletedAt = DateTimeOffset.UtcNow;
        this.FailureReason = null;
    }

    /// <summary>
    ///     Marks the job as failed with the specified failure reason.
    /// </summary>
    /// <param name="failureReason">The reason for the failure.</param>
    /// <exception cref="ArgumentException">Thrown when failure reason is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job is not in pending or processing status.</exception>
    public void MarkFailed(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        if (this.Status is not (ProCursorIndexJobStatus.Pending or ProCursorIndexJobStatus.Processing))
        {
            throw new InvalidOperationException($"Cannot fail a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Failed;
        this.CompletedAt = DateTimeOffset.UtcNow;
        this.FailureReason = failureReason.Trim();
    }

    /// <summary>
    ///     Marks the job as pending for retry with the specified failure reason.
    /// </summary>
    /// <param name="failureReason">The reason for the retry.</param>
    /// <exception cref="ArgumentException">Thrown when failure reason is null or whitespace.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the job is not in processing status.</exception>
    public void MarkPendingForRetry(string failureReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureReason);

        if (this.Status != ProCursorIndexJobStatus.Processing)
        {
            throw new InvalidOperationException($"Cannot retry a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Pending;
        this.StartedAt = null;
        this.CompletedAt = null;
        this.FailureReason = failureReason.Trim();
    }

    /// <summary>
    ///     Marks the job as superseded.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the job is already completed or cancelled.</exception>
    public void MarkSuperseded()
    {
        if (this.Status is ProCursorIndexJobStatus.Completed or ProCursorIndexJobStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot supersede a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Superseded;
        this.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Marks the job as cancelled.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the job is already completed or superseded.</exception>
    public void MarkCancelled()
    {
        if (this.Status is ProCursorIndexJobStatus.Completed or ProCursorIndexJobStatus.Superseded)
        {
            throw new InvalidOperationException($"Cannot cancel a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Cancelled;
        this.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
