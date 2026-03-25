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
    public PartialReviewFailureException(int failedCount, int totalCount, IEnumerable<Exception> innerExceptions)
        : base($"Review failed for {failedCount} of {totalCount} files.", innerExceptions)
    {
        this.FailedCount = failedCount;
        this.TotalCount = totalCount;
    }

    /// <summary>Number of file review passes that failed.</summary>
    public int FailedCount { get; }

    /// <summary>Total number of file review passes attempted.</summary>
    public int TotalCount { get; }
}
