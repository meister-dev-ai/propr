using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Thrown when one or more file review passes fail, but the overall job can be retried.
/// </summary>
public sealed class PartialReviewFailureException : AggregateException
{
    /// <summary>
    ///     Initializes a new <see cref="PartialReviewFailureException" />.
    /// </summary>
    /// <param name="failedCount">Number of files that failed.</param>
    /// <param name="totalCount">Total number of files in the review.</param>
    /// <param name="innerExceptions">The exceptions for each failed file pass.</param>
    /// <param name="partialResult">
    ///     A synthesis result built from the files that <em>did</em> succeed, or
    ///     <see langword="null" /> when synthesis was not attempted or itself failed.
    /// </param>
    public PartialReviewFailureException(
        int failedCount,
        int totalCount,
        IEnumerable<Exception> innerExceptions,
        ReviewResult? partialResult = null)
        : base($"Review failed for {failedCount} of {totalCount} files.", innerExceptions)
    {
        this.FailedCount = failedCount;
        this.TotalCount = totalCount;
        this.PartialResult = partialResult;
    }

    /// <summary>Number of file review passes that failed.</summary>
    public int FailedCount { get; }

    /// <summary>Total number of file review passes attempted.</summary>
    public int TotalCount { get; }

    /// <summary>
    ///     A synthesis result assembled from the files that were successfully reviewed before the
    ///     exception was thrown. <see langword="null" /> when synthesis was not attempted or failed.
    ///     Used by the orchestration service to post partial results when max retries are exhausted.
    /// </summary>
    public ReviewResult? PartialResult { get; }
}
