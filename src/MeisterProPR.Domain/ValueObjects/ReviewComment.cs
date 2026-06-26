// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
}
