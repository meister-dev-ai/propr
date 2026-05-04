// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.CommentRelevance.Fixtures;

[Flags]
internal enum RepresentativeFilterCommentCategory
{
    None = 0,
    ConfirmedValid = 1,
    KnownFalsePositive = 2,
    UnsupportedSpeculativeHighSeverity = 4,
}

internal enum RepresentativeHybridEvaluationMode
{
    NotRequired,
    Successful,
    Unavailable,
}

internal enum RepresentativeHybridDecision
{
    Keep,
    Discard,
}

internal sealed record RepresentativeFilterEvaluationComment(
    string CommentId,
    ReviewComment Comment,
    RepresentativeFilterCommentCategory Categories,
    RepresentativeHybridDecision? HybridDecision = null,
    string? HybridDiscardReasonCode = null);

internal sealed record RepresentativeFilterEvaluationCase(
    string CaseId,
    string FilePath,
    IReadOnlyList<RepresentativeFilterEvaluationComment> Comments,
    RepresentativeHybridEvaluationMode HybridEvaluationMode = RepresentativeHybridEvaluationMode.NotRequired,
    long HybridInputTokens = 0,
    long HybridOutputTokens = 0,
    string? HybridDegradedCause = null);

internal static class RepresentativeFilterEvaluationCorpus
{
    public const string Version = "v1";

    public static IReadOnlyList<RepresentativeFilterEvaluationCase> Cases { get; } =
    [
        SingleCommentCase(
            "anchored-null-dereference",
            "AnchoredNullDeref.cs",
            8,
            CommentSeverity.Warning,
            "Confirmed null dereference in `ExecuteAsync()` before validation at line 8.",
            RepresentativeFilterCommentCategory.ConfirmedValid),
        SingleCommentCase(
            "anchored-uninitialized-state",
            "AnchoredUninitializedState.cs",
            14,
            CommentSeverity.Error,
            "`state` is dereferenced before initialization at line 14 in `ApplyAsync()`.",
            RepresentativeFilterCommentCategory.ConfirmedValid),
        SingleCommentCase(
            "ambiguous-valid-caller-contract",
            "AmbiguousValidCallerContract.cs",
            null,
            CommentSeverity.Warning,
            "The caller `PipelineBuilder.Build()` passes `null` into `ExecuteAsync()`, leaving this dereference unguarded.",
            RepresentativeFilterCommentCategory.ConfirmedValid,
            RepresentativeHybridEvaluationMode.Successful,
            RepresentativeHybridDecision.Keep,
            hybridInputTokens: 90,
            hybridOutputTokens: 20),
        SingleCommentCase(
            "summary-only-noise",
            "SummaryOnlyNoise.cs",
            null,
            CommentSeverity.Suggestion,
            "Overall this file could be cleaned up across multiple places.",
            RepresentativeFilterCommentCategory.KnownFalsePositive),
        SingleCommentCase(
            "speculative-missing-concrete-warning",
            "SpeculativeMissingConcreteWarning.cs",
            null,
            CommentSeverity.Warning,
            "behavior is broken in some cases.",
            RepresentativeFilterCommentCategory.KnownFalsePositive | RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity,
            RepresentativeHybridEvaluationMode.Successful,
            RepresentativeHybridDecision.Discard,
            CommentRelevanceReasonCodes.MissingConcreteObservable,
            95,
            24),
        SingleCommentCase(
            "cross-file-critical-error",
            "CrossFileCriticalError.cs",
            null,
            CommentSeverity.Error,
            "Critical issue may exist in another file.",
            RepresentativeFilterCommentCategory.KnownFalsePositive | RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity,
            RepresentativeHybridEvaluationMode.Successful,
            RepresentativeHybridDecision.Discard,
            CommentRelevanceReasonCodes.UnverifiableCrossFileClaim,
            110,
            27),
        SingleCommentCase(
            "tooling-limitation-noise",
            "ToolingLimitationNoise.cs",
            null,
            CommentSeverity.Warning,
            "The tool output was truncated so this might be a defect.",
            RepresentativeFilterCommentCategory.KnownFalsePositive),
        SingleCommentCase(
            "outage-fallback-warning",
            "OutageFallbackWarning.cs",
            null,
            CommentSeverity.Warning,
            "Another file initializes this differently.",
            RepresentativeFilterCommentCategory.KnownFalsePositive | RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity,
            RepresentativeHybridEvaluationMode.Unavailable,
            hybridDegradedCause: "Comment relevance evaluator timed out."),
        SingleCommentCase(
            "hedged-high-severity-warning",
            "HedgedHighSeverityWarning.cs",
            null,
            CommentSeverity.Warning,
            "This likely fails when configuration is missing.",
            RepresentativeFilterCommentCategory.KnownFalsePositive | RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity),
        SingleCommentCase(
            "severe-cross-file-warning",
            "SevereCrossFileWarning.cs",
            null,
            CommentSeverity.Warning,
            "Guaranteed data loss may occur in another file.",
            RepresentativeFilterCommentCategory.KnownFalsePositive | RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity,
            RepresentativeHybridEvaluationMode.Successful,
            RepresentativeHybridDecision.Discard,
            CommentRelevanceReasonCodes.UnverifiableCrossFileClaim,
            120,
            29),
    ];

    private static RepresentativeFilterEvaluationCase SingleCommentCase(
        string caseId,
        string fileName,
        int? lineNumber,
        CommentSeverity severity,
        string message,
        RepresentativeFilterCommentCategory categories,
        RepresentativeHybridEvaluationMode hybridEvaluationMode = RepresentativeHybridEvaluationMode.NotRequired,
        RepresentativeHybridDecision? hybridDecision = null,
        string? hybridDiscardReasonCode = null,
        long hybridInputTokens = 0,
        long hybridOutputTokens = 0,
        string? hybridDegradedCause = null)
    {
        var filePath = $"src/RepresentativeCorpus/{fileName}";

        return new RepresentativeFilterEvaluationCase(
            caseId,
            filePath,
            [
                new RepresentativeFilterEvaluationComment(
                    $"{caseId}-comment-0",
                    CommentRelevanceFilterTestData.CreateComment(message, severity, lineNumber, filePath),
                    categories,
                    hybridDecision,
                    hybridDiscardReasonCode),
            ],
            hybridEvaluationMode,
            hybridInputTokens,
            hybridOutputTokens,
            hybridDegradedCause);
    }
}
