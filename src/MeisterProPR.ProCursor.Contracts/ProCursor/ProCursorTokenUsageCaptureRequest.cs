// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Safe write-side payload used to persist one ProCursor token usage event.
/// </summary>
public sealed record ProCursorTokenUsageCaptureRequest(
    Guid ClientId,
    Guid ProCursorSourceId,
    string SourceDisplayNameSnapshot,
    string RequestId,
    DateTimeOffset OccurredAtUtc,
    ProCursorTokenUsageCallType CallType,
    string DeploymentName,
    string ModelName,
    string TokenizerName,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    bool TokensEstimated,
    decimal? EstimatedCostUsd,
    bool CostEstimated,
    Guid? AiConnectionId = null,
    Guid? IndexJobId = null,
    string? ResourceId = null,
    string? SourcePath = null,
    Guid? KnowledgeChunkId = null,
    string? SafeMetadataJson = null);

/// <summary>
///     Optional per-input safe metadata used when an embedding request spans multiple source texts.
/// </summary>
public sealed record ProCursorTokenUsageInputContext(
    string? SourcePath = null,
    string? ResourceId = null,
    Guid? KnowledgeChunkId = null);

/// <summary>
///     Context used by the embedding service to attribute one physical embedding call to a ProCursor source.
/// </summary>
public sealed record ProCursorEmbeddingUsageContext(
    Guid ProCursorSourceId,
    string SourceDisplayNameSnapshot,
    string RequestIdPrefix,
    ProCursorTokenUsageCallType CallType = ProCursorTokenUsageCallType.Embedding,
    Guid? IndexJobId = null,
    IReadOnlyList<ProCursorTokenUsageInputContext>? InputContexts = null);

/// <summary>
///     Result returned after retention cleanup for ProCursor token reporting.
/// </summary>
public sealed record ProCursorTokenUsageRetentionResult(
    int DeletedEventCount,
    int DeletedRollupCount,
    DateTimeOffset PerformedAtUtc);
