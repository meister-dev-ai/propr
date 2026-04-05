// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
