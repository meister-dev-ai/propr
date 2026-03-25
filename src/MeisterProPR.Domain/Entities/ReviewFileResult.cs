using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Entities;

public class ReviewFileResult
{
    private ReviewFileResult()
    {
    } // EF Core

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

    public Guid Id { get; private set; }
    public Guid JobId { get; private set; }
    public string FilePath { get; private set; } = null!;
    public bool IsComplete { get; private set; }
    public bool IsFailed { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? PerFileSummary { get; private set; }
    public IReadOnlyList<ReviewComment>? Comments { get; private set; }

    public void MarkCompleted(string summary, IReadOnlyList<ReviewComment> comments)
    {
        if (this.IsFailed)
        {
            throw new InvalidOperationException("Cannot complete a failed result");
        }

        this.IsComplete = true;
        this.PerFileSummary = summary;
        this.Comments = comments;
    }

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
    ///     Resets an interrupted (not complete, not failed) result so it can be re-attempted.
    ///     Called when a previous processing run was killed mid-flight.
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
