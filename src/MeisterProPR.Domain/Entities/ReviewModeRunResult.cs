// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Entities;

/// <summary>Durable result for one review strategy execution within a job or comparison group.</summary>
public sealed class ReviewModeRunResult
{
    private readonly List<ReviewStageMetrics> _stageMetrics = [];

    private ReviewModeRunResult()
    {
    }

    public ReviewModeRunResult(
        Guid id,
        Guid reviewJobId,
        ReviewStrategy strategy,
        ReviewPublicationMode publicationMode,
        Guid? comparisonGroupId = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (reviewJobId == Guid.Empty)
        {
            throw new ArgumentException("Review job id must not be empty.", nameof(reviewJobId));
        }

        this.Id = id;
        this.ReviewJobId = reviewJobId;
        this.Strategy = strategy;
        this.PublicationMode = publicationMode;
        this.ComparisonGroupId = comparisonGroupId;
        this.Status = JobStatus.Pending;
    }

    public Guid Id { get; init; }

    public Guid ReviewJobId { get; init; }

    public Guid? ComparisonGroupId { get; private set; }

    public ReviewStrategy Strategy { get; private set; }

    public ReviewPublicationMode PublicationMode { get; private set; }

    public JobStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public ReviewResult? Result { get; private set; }

    public IReadOnlyList<ReviewStageMetrics> StageMetrics => this._stageMetrics.AsReadOnly();

    public void MarkProcessing(DateTimeOffset startedAt)
    {
        this.Status = JobStatus.Processing;
        this.StartedAt = startedAt;
    }

    public void MarkCompleted(ReviewResult result, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        EnsureCompletionTimestampIsValid(completedAt);
        this.Result = result;
        this.CompletedAt = completedAt;
        this.Status = JobStatus.Completed;
    }

    public void MarkFailed(DateTimeOffset completedAt)
    {
        EnsureCompletionTimestampIsValid(completedAt);
        this.CompletedAt = completedAt;
        this.Status = JobStatus.Failed;
    }

    public void AddStageMetrics(ReviewStageMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        this._stageMetrics.Add(metrics);
    }

    private void EnsureCompletionTimestampIsValid(DateTimeOffset completedAt)
    {
        if (this.StartedAt != default && completedAt < this.StartedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(completedAt), "Completion timestamp must be on or after the start timestamp.");
        }
    }
}
