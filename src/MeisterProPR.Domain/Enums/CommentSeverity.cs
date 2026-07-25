// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Severity level of a review comment.
/// </summary>
public enum CommentSeverity
{
    // Persisted by numeric value in the clients.minimum_severity_to_post column — keep these values explicit and do
    // NOT reorder or renumber, or stored thresholds would silently remap. The post-threshold ordering is defined by
    // CommentSeverityRanking, deliberately independent of these numeric values.

    /// <summary>Informational comment.</summary>
    Info = 0,

    /// <summary>Potential issue that should be reviewed.</summary>
    Warning = 1,

    /// <summary>Definite error.</summary>
    Error = 2,

    /// <summary>Suggestion for improvement.</summary>
    Suggestion = 3,
}
