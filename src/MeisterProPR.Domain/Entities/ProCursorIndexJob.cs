// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Durable queue record for full or incremental ProCursor indexing work.
/// </summary>
public sealed class ProCursorIndexJob
{
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

    public Guid Id { get; private set; }

    public Guid KnowledgeSourceId { get; private set; }

    public Guid TrackedBranchId { get; private set; }

    public string? RequestedCommitSha { get; private set; }

    public string JobKind { get; private set; } = string.Empty;

    public ProCursorIndexJobStatus Status { get; private set; }

    public string DedupKey { get; private set; } = string.Empty;

    public int AttemptCount { get; private set; }

    public DateTimeOffset QueuedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public string? FailureReason { get; private set; }

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

    public void MarkSuperseded()
    {
        if (this.Status is ProCursorIndexJobStatus.Completed or ProCursorIndexJobStatus.Cancelled)
        {
            throw new InvalidOperationException($"Cannot supersede a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Superseded;
        this.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    public void MarkCancelled()
    {
        if (this.Status is ProCursorIndexJobStatus.Completed or ProCursorIndexJobStatus.Superseded)
        {
            throw new InvalidOperationException($"Cannot cancel a job from status {this.Status}.");
        }

        this.Status = ProCursorIndexJobStatus.Cancelled;
        this.CompletedAt ??= DateTimeOffset.UtcNow;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }
}
