// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable machine-readable reason codes used by comment relevance filters.
/// </summary>
public static class CommentRelevanceReasonCodes
{
    public const string HedgingLanguage = "hedging_language";
    public const string NonActionableSuggestion = "non_actionable_suggestion";
    public const string SeverityOverstated = "severity_overstated";
    public const string WrongFileOrAnchor = "wrong_file_or_anchor";
    public const string UnverifiableCrossFileClaim = "unverifiable_cross_file_claim";
    public const string MissingConcreteObservable = "missing_concrete_observable";
    public const string SummaryLevelOnly = "summary_level_only";
    public const string DuplicateLocalPattern = "duplicate_local_pattern";
    public const string ToolingLimitationMisclassified = "tooling_limitation_misclassified";
}
