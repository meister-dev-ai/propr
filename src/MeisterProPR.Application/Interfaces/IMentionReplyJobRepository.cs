using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persists and manages mention reply jobs.
/// </summary>
public interface IMentionReplyJobRepository
{
    /// <summary>Adds a new mention reply job.</summary>
    /// <param name="job">The job to persist.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task AddAsync(MentionReplyJob job, CancellationToken ct = default);

    /// <summary>Returns all pending jobs, oldest first.</summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<IReadOnlyList<MentionReplyJob>> GetPendingAsync(CancellationToken ct = default);

    /// <summary>
    ///     Returns <c>true</c> if a job already exists for the given mention comment.
    ///     Used to prevent duplicate enqueuing.
    /// </summary>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="pullRequestId">ADO pull request number.</param>
    /// <param name="threadId">ADO thread ID.</param>
    /// <param name="commentId">ADO comment ID.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> ExistsForCommentAsync(
        Guid clientId,
        int pullRequestId,
        int threadId,
        int commentId,
        CancellationToken ct = default);

    /// <summary>
    ///     Atomic compare-and-swap on <see cref="MentionJobStatus" />.
    ///     Returns <c>false</c> if the current status does not equal <paramref name="from" />.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="from">The expected current status.</param>
    /// <param name="to">The new status to set if the current status matches.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task<bool> TryTransitionAsync(
        Guid jobId,
        MentionJobStatus from,
        MentionJobStatus to,
        CancellationToken ct = default);

    /// <summary>Marks a job as failed with an error message.</summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="errorMessage">A message describing the reason for the failure.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default);

    /// <summary>Marks a job as successfully completed.</summary>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task SetCompletedAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    ///     Transitions all <see cref="MentionJobStatus.Processing" /> jobs back to
    ///     <see cref="MentionJobStatus.Pending" />. Called at startup to recover jobs
    ///     that were in-flight when the process last terminated.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task ResetStuckProcessingAsync(CancellationToken ct = default);
}
