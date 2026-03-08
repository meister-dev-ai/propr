using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a single review comment produced by the review engine.
/// </summary>
public sealed record ReviewComment
{
    /// <summary>
    ///     Creates a new <see cref="ReviewComment" />.
    /// </summary>
    public ReviewComment(string? filePath, int? lineNumber, CommentSeverity severity, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw new ArgumentException("Message required.", nameof(message));
        }

        this.FilePath = filePath;
        this.LineNumber = lineNumber;
        this.Severity = severity;
        this.Message = message;
    }

    /// <summary>Optional file path the comment refers to.</summary>
    public string? FilePath { get; init; }

    /// <summary>Optional line number within the file.</summary>
    public int? LineNumber { get; init; }

    /// <summary>Severity of the comment.</summary>
    public CommentSeverity Severity { get; init; }

    /// <summary>Comment message text.</summary>
    public string Message { get; init; }
}