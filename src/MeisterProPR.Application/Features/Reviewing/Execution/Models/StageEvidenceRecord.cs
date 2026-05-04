// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Normalized evidence record for one review workflow stage.
/// </summary>
public sealed record StageEvidenceRecord(
    string StageId,
    string Label,
    string? RelatedFilePath,
    string Outcome,
    int? IterationCount,
    int? ToolCallCount,
    long? InputTokens,
    long? OutputTokens,
    int? FinalConfidence,
    string? ModelId,
    AiConnectionModelCategory? ConnectionCategory,
    IReadOnlyList<StageEvidenceEvent> Events);
