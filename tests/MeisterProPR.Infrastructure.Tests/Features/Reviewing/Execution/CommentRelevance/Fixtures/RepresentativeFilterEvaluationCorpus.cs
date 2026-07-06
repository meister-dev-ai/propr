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
        // Concrete, correctly-anchored findings: kept by every implementation.
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

        // A genuinely valid finding whose only mechanical objection is that it spans two files. The
        // deterministic heuristic discards it (it cannot verify a cross-file claim on its own); the hybrid
        // evaluator opens the files, confirms it, and keeps it — so hybrid retains more valid findings.
        SingleCommentCase(
            "cross-file-valid-caller-contract",
            "CrossFileValidCallerContract.cs",
            null,
            CommentSeverity.Warning,
            "The caller in `PipelineBuilder.cs` passes null into the guard defined in `ExecuteAsync.cs`, leaving this dereference unguarded.",
            RepresentativeFilterCommentCategory.ConfirmedValid,
            RepresentativeHybridEvaluationMode.Successful,
            RepresentativeHybridDecision.Keep,
            hybridInputTokens: 90,
            hybridOutputTokens: 20),

        // Speculative WARNING with no concrete observable (no line, no code token): discarded
        // deterministically and confirmed discarded by the hybrid evaluator.
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

        // Unverifiable cross-file ERROR (two distinct file references): discarded deterministically and
        // confirmed discarded by the evaluator.
        SingleCommentCase(
            "unverifiable-cross-file-error",
            "UnverifiableCrossFileError.cs",
            null,
            CommentSeverity.Error,
            "Data loss occurs where `ReviewArchiveStore.cs` writes a value that `GetFileDiffHandler.cs` reads back inconsistently.",
            RepresentativeFilterCommentCategory.KnownFalsePositive | RepresentativeFilterCommentCategory.UnsupportedSpeculativeHighSeverity,
            RepresentativeHybridEvaluationMode.Successful,
            RepresentativeHybridDecision.Discard,
            CommentRelevanceReasonCodes.UnverifiableCrossFileClaim,
            120,
            29),

        // Cross-file claim routed to the evaluator during an outage: the hybrid filter conservatively keeps
        // the ambiguous survivor and records a degraded run, while the deterministic heuristic still discards it.
        SingleCommentCase(
            "cross-file-outage-fallback",
            "CrossFileOutageFallback.cs",
            null,
            CommentSeverity.Warning,
            "Initialization differs between `StartupModule.cs` and `RuntimeModule.cs`, so this path may misbehave.",
            RepresentativeFilterCommentCategory.KnownFalsePositive,
            RepresentativeHybridEvaluationMode.Unavailable,
            hybridDegradedCause: "Comment relevance evaluator timed out."),
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
