// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Stable identity for a provider discussion thread anchored to a review.</summary>
public sealed record ReviewThreadRef
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReviewThreadRef"/> class.
    /// </summary>
    /// <param name="review">The code review reference.</param>
    /// <param name="externalThreadId">The external thread identifier.</param>
    /// <param name="filePath">The file path associated with the thread.</param>
    /// <param name="lineNumber">The line number associated with the thread.</param>
    /// <param name="isReviewerOwned">Whether the thread is owned by the reviewer.</param>
    public ReviewThreadRef(
        CodeReviewRef review,
        string externalThreadId,
        string? filePath,
        int? lineNumber,
        bool isReviewerOwned)
    {
        this.Review = review ?? throw new ArgumentNullException(nameof(review));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalThreadId);

        if (lineNumber.HasValue)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(lineNumber.Value, 1);
        }

        this.ExternalThreadId = externalThreadId.Trim();
        this.FilePath = string.IsNullOrWhiteSpace(filePath) ? null : filePath.Trim();
        this.LineNumber = lineNumber;
        this.IsReviewerOwned = isReviewerOwned;
    }

    /// <summary>Gets the code review reference.</summary>
    public CodeReviewRef Review { get; }

    /// <summary>Gets the external thread identifier.</summary>
    public string ExternalThreadId { get; }

    /// <summary>Gets the file path associated with the thread.</summary>
    public string? FilePath { get; }

    /// <summary>Gets the line number associated with the thread.</summary>
    public int? LineNumber { get; }

    /// <summary>Gets a value indicating whether the thread is owned by the reviewer.</summary>
    public bool IsReviewerOwned { get; }
}
