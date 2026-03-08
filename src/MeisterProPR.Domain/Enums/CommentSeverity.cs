namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Severity level of a review comment.
/// </summary>
public enum CommentSeverity
{
    /// <summary>Informational comment.</summary>
    Info,

    /// <summary>Potential issue that should be reviewed.</summary>
    Warning,

    /// <summary>Definite error.</summary>
    Error,

    /// <summary>Suggestion for improvement.</summary>
    Suggestion,
}