// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Deterministic classification of where a review comment's anchor line falls relative to the
///     pull request's changed-line ranges for its file. Provenance metadata only — it does not
///     participate in deduplication and is <see langword="null" /> when the comment could not be
///     classified (unknown line or a file with no resolvable changed ranges).
/// </summary>
public enum ReviewCommentScopeRelation
{
    // Persisted by ordinal in the review-result jsonb column — keep these values explicit and do NOT
    // reorder or renumber, or historical comment rows would silently remap to a different relation.

    /// <summary>The comment's line falls inside one of the file's changed ranges.</summary>
    OnChangedLine = 0,

    /// <summary>The comment's line is within a few lines of a changed range and treated as in-scope.</summary>
    AdjacentToChange = 1,

    /// <summary>The comment's line lies in pre-existing code far from every changed range.</summary>
    OutsideChange = 2,
}

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
    public string? FilePath { get; }

    /// <summary>Optional line number within the file.</summary>
    public int? LineNumber { get; }

    /// <summary>Severity of the comment.</summary>
    public CommentSeverity Severity { get; }

    /// <summary>Comment message text.</summary>
    public string Message { get; }

    /// <summary>
    ///     The <c>ReviewPassKind</c> name of the review pass that produced this finding (e.g.
    ///     <c>"Baseline"</c>, <c>"ProRVAugmentation"</c>), when known. Provenance metadata only:
    ///     it does not participate in deduplication (which keys on message text) and is
    ///     <see langword="null" /> for legacy comments and comments with no recorded origin.
    /// </summary>
    public string? OriginPassKind { get; init; }

    /// <summary>
    ///     Deterministic classification of this comment's anchor line relative to the pull request's
    ///     changed-line ranges. Provenance metadata only: it does not participate in deduplication
    ///     (which keys on message text) and is <see langword="null" /> for legacy comments and comments
    ///     that could not be classified (unknown line or a file with no resolvable changed ranges).
    /// </summary>
    public ReviewCommentScopeRelation? ScopeRelation { get; init; }
}
