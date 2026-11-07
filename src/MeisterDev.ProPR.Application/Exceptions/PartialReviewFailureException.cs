// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Exceptions;

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
    ///     <see langword="null" /> when no file was reviewed, when synthesis was not attempted, or when synthesis
    ///     itself failed.
    /// </param>
    /// <param name="failedReviewFilePaths">
    ///     The paths of the files whose review failed, in the order of <paramref name="innerExceptions" />. The
    ///     message names them.
    /// </param>
    /// <param name="reviewedFileCount">
    ///     The number of files the job reviewed in this attempt and in the attempts before it, or
    ///     <see langword="null" /> when the files were not counted.
    /// </param>
    public PartialReviewFailureException(
        int failedCount,
        int totalCount,
        IEnumerable<Exception> innerExceptions,
        ReviewResult? partialResult = null,
        IReadOnlyList<string>? failedReviewFilePaths = null,
        int? reviewedFileCount = null)
        : base(FormatMessage(failedCount, totalCount, failedReviewFilePaths), innerExceptions)
    {
        this.FailedCount = failedCount;
        this.TotalCount = totalCount;
        this.PartialResult = partialResult;
        this.FailedReviewFilePaths = failedReviewFilePaths ?? [];
        this.ReviewedFileCount = reviewedFileCount;
    }

    /// <summary>Number of file review passes that failed.</summary>
    public int FailedCount { get; }

    /// <summary>Total number of file review passes attempted.</summary>
    public int TotalCount { get; }

    /// <summary>
    ///     A synthesis result assembled from the files that were successfully reviewed before the
    ///     exception was thrown. <see langword="null" /> when no file was reviewed, when synthesis was not
    ///     attempted, or when it failed. Used by the orchestration service to post partial results when max
    ///     retries are exhausted.
    /// </summary>
    public ReviewResult? PartialResult { get; }

    /// <summary>The paths of the files whose review failed. Empty when the paths were not recorded.</summary>
    public IReadOnlyList<string> FailedReviewFilePaths { get; }

    /// <summary>
    ///     The number of files the job reviewed in this attempt and in the attempts before it. Zero means that no
    ///     file was reviewed, and the orchestration service then publishes nothing. <see langword="null" /> when the
    ///     files were not counted.
    /// </summary>
    public int? ReviewedFileCount { get; }

    private static string FormatMessage(int failedCount, int totalCount, IReadOnlyList<string>? failedReviewFilePaths)
    {
        var counts = string.Create(CultureInfo.InvariantCulture, $"Review failed for {failedCount} of {totalCount} files");
        return failedReviewFilePaths is { Count: > 0 }
            ? $"{counts}: {string.Join(", ", failedReviewFilePaths)}."
            : $"{counts}.";
    }
}
