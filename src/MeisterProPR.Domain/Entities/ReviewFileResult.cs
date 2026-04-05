// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Stores the per-file outcome for a review job, including completion, failure, and exclusion state.
/// </summary>
public class ReviewFileResult
{
    private ReviewFileResult()
    {
    } // EF Core

    /// <summary>
    ///     Creates a new per-file result row for the given review job and file path.
    /// </summary>
    /// <param name="jobId">The owning review job identifier.</param>
    /// <param name="filePath">The changed file path represented by this result row.</param>
    public ReviewFileResult(Guid jobId, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty", nameof(filePath));
        }

        this.Id = Guid.NewGuid();
        this.JobId = jobId;
        this.FilePath = filePath;
    }

    /// <summary>The unique identifier of this file result row.</summary>
    public Guid Id { get; private set; }

    /// <summary>The owning review job identifier.</summary>
    public Guid JobId { get; private set; }

    /// <summary>The repository-relative file path being reviewed.</summary>
    public string FilePath { get; private set; } = null!;

    /// <summary>True when the file review reached a terminal successful state.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>True when the file review failed and captured an error message.</summary>
    public bool IsFailed { get; private set; }

    /// <summary>True when the file was excluded from AI review by repository rules.</summary>
    public bool IsExcluded { get; private set; }

    /// <summary>
    ///     True when this result was inherited from a prior iteration's completed job
    ///     rather than freshly computed in the current review pass.
    /// </summary>
    public bool IsCarriedForward { get; private set; }

    /// <summary>The exclusion rule that matched this file, when <see cref="IsExcluded" /> is true.</summary>
    public string? ExclusionReason { get; private set; }

    /// <summary>The terminal error captured when <see cref="IsFailed" /> is true.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>The per-file AI summary, when the review completed successfully.</summary>
    public string? PerFileSummary { get; private set; }

    /// <summary>The AI review comments for this file, or null until completion.</summary>
    public IReadOnlyList<ReviewComment>? Comments { get; private set; }

    /// <summary>
    ///     Creates a <see cref="ReviewFileResult" /> that carries forward the result from a prior
    ///     iteration without dispatching a new AI review for the file.
    /// </summary>
    /// <param name="jobId">The ID of the new review job that owns this carried-forward result.</param>
    /// <param name="prior">The completed result from the prior iteration to copy from.</param>
    public static ReviewFileResult CreateCarriedForward(Guid jobId, ReviewFileResult prior)
    {
        var result = new ReviewFileResult(jobId, prior.FilePath);
        result.IsComplete = true;
        result.IsCarriedForward = true;
        result.PerFileSummary = prior.PerFileSummary;
        result.Comments = prior.Comments;
        return result;
    }

    /// <summary>
    ///     Marks the file result as successfully completed and stores the summary and comments.
    /// </summary>
    /// <param name="summary">The AI-generated per-file summary.</param>
    /// <param name="comments">The AI-generated review comments for the file.</param>
    public void MarkCompleted(string summary, IReadOnlyList<ReviewComment> comments)
    {
        if (this.IsFailed)
        {
            throw new InvalidOperationException("Cannot complete a failed result");
        }

        if (this.IsComplete)
        {
            throw new InvalidOperationException("Cannot complete an already-complete result");
        }

        this.IsComplete = true;
        this.PerFileSummary = summary;
        this.Comments = comments;
    }

    /// <summary>
    ///     Marks the file result as failed and stores the terminal error message.
    /// </summary>
    /// <param name="errorMessage">The error explaining why the file review failed.</param>
    public void MarkFailed(string errorMessage)
    {
        if (this.IsComplete)
        {
            throw new InvalidOperationException("Cannot fail a completed result");
        }

        this.IsFailed = true;
        this.ErrorMessage = errorMessage;
    }

    /// <summary>
    ///     Marks this file result as excluded — no AI review was performed.
    ///     Sets <see cref="IsComplete" /> to <see langword="true" /> so the file is treated as
    ///     a terminal state and is not retried.
    /// </summary>
    /// <param name="exclusionReason">The glob pattern that matched this file, for display purposes.</param>
    /// <exception cref="InvalidOperationException">
    ///     Thrown when the result is already in a terminal state (<see cref="IsFailed" /> or
    ///     <see cref="IsComplete" />). Call <see cref="ResetForRetry" /> first if the row was
    ///     previously failed and needs to be re-classified as excluded.
    /// </exception>
    public void MarkExcluded(string exclusionReason)
    {
        if (this.IsFailed)
        {
            throw new InvalidOperationException("Cannot exclude a failed result; call ResetForRetry() first.");
        }

        if (this.IsComplete)
        {
            throw new InvalidOperationException("Cannot exclude an already-completed result.");
        }

        this.IsExcluded = true;
        this.IsComplete = true;
        this.ExclusionReason = exclusionReason;
    }

    /// <summary>
    ///     Resets a non-terminal result (interrupted mid-flight or previously failed) so it can be
    ///     re-attempted.  Safe to call on any row that has <see cref="IsComplete" /> equal to
    ///     <see langword="false" />, which covers both killed-in-progress and failed rows.
    /// </summary>
    public void ResetForRetry()
    {
        this.IsComplete = false;
        this.IsFailed = false;
        this.ErrorMessage = null;
        this.PerFileSummary = null;
        this.Comments = null;
    }
}
