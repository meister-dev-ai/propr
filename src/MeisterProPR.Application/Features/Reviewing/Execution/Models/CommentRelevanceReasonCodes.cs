// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable machine-readable reason codes used by comment relevance filters.
/// </summary>
public static class CommentRelevanceReasonCodes
{
    /// <summary>
    ///     Reason code used when a comment relies on hedging language.
    /// </summary>
    public const string HedgingLanguage = "hedging_language";

    /// <summary>
    ///     Reason code used when a comment suggests a non-actionable change.
    /// </summary>
    public const string NonActionableSuggestion = "non_actionable_suggestion";

    /// <summary>
    ///     Reason code used when a comment overstates severity.
    /// </summary>
    public const string SeverityOverstated = "severity_overstated";

    /// <summary>
    ///     Reason code used when a comment points at the wrong file or anchor.
    /// </summary>
    public const string WrongFileOrAnchor = "wrong_file_or_anchor";

    /// <summary>
    ///     Reason code used when a cross-file claim cannot be verified.
    /// </summary>
    public const string UnverifiableCrossFileClaim = "unverifiable_cross_file_claim";

    /// <summary>
    ///     Reason code used when a comment lacks a concrete observable.
    /// </summary>
    public const string MissingConcreteObservable = "missing_concrete_observable";

    /// <summary>
    ///     Reason code used when a comment stays at summary level only.
    /// </summary>
    public const string SummaryLevelOnly = "summary_level_only";

    /// <summary>
    ///     Reason code used when a comment duplicates an already-covered local pattern.
    /// </summary>
    public const string DuplicateLocalPattern = "duplicate_local_pattern";

    /// <summary>
    ///     Reason code used when a tooling limitation is misclassified as a real issue.
    /// </summary>
    public const string ToolingLimitationMisclassified = "tooling_limitation_misclassified";
}
