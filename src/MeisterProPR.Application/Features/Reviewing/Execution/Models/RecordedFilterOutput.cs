// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Stable per-file comparison record emitted by every comment relevance filter implementation.
/// </summary>
public sealed record RecordedFilterOutput(
    string ImplementationId,
    string ImplementationVersion,
    string FilePath,
    int OriginalCommentCount,
    int KeptCount,
    int DiscardedCount,
    IReadOnlyDictionary<string, int> ReasonBuckets,
    IReadOnlyDictionary<string, int> DecisionSources,
    IReadOnlyList<RecordedDiscardedFilterComment> Discarded,
    IReadOnlyList<string> DegradedComponents,
    IReadOnlyList<string> FallbackChecks,
    string? DegradedCause,
    FilterAiTokenUsage? AiTokenUsage);

/// <summary>
///     The recorded discard-detail shape used by diagnostics and comparison workflows.
/// </summary>
public sealed record RecordedDiscardedFilterComment(
    string FilePath,
    int? LineNumber,
    string Severity,
    string Message,
    IReadOnlyList<string> ReasonCodes,
    string DecisionSource);
