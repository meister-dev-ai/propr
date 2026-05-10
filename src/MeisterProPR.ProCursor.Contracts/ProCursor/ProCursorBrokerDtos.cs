// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.DTOs.ProCursor;

/// <summary>
///     Request used by the extracted ProCursor host to ask ProPR for source materialization data.
/// </summary>
public sealed record ProCursorScmMaterializationRequest(
    ProCursorKnowledgeSourceDto Source,
    ProCursorTrackedBranchDto TrackedBranch,
    string? RequestedCommitSha = null);

/// <summary>
///     One text file returned through the ProPR SCM broker.
/// </summary>
public sealed record ProCursorScmFileDto(string Path, string Content);

/// <summary>
///     Materialized source payload returned through the ProPR SCM broker.
/// </summary>
public sealed record ProCursorScmMaterializationResponse(
    string CommitSha,
    IReadOnlyList<ProCursorScmFileDto> Files);

/// <summary>
///     Request used by the extracted ProCursor host to resolve the latest tracked-branch commit.
/// </summary>
public sealed record ProCursorTrackedBranchHeadRequest(
    ProCursorKnowledgeSourceDto Source,
    ProCursorTrackedBranchDto TrackedBranch);

/// <summary>
///     Response returned by the ProPR SCM broker for branch-head resolution.
/// </summary>
public sealed record ProCursorTrackedBranchHeadResponse(string? CommitSha);

/// <summary>
///     Request used to resolve the active embedding deployment for a client.
/// </summary>
public sealed record ProCursorEmbeddingDeploymentRequest(Guid ClientId, int? ExpectedDimensions = null);

/// <summary>
///     Active embedding deployment metadata returned by ProPR.
/// </summary>
public sealed record ProCursorEmbeddingDeploymentDto(
    Guid? AiConnectionId,
    string DeploymentName,
    string TokenizerName,
    int MaxInputTokens,
    int EmbeddingDimensions,
    decimal? InputCostPer1MUsd = null,
    decimal? OutputCostPer1MUsd = null);

/// <summary>
///     Request used to generate one embedding vector per input string.
/// </summary>
public sealed record ProCursorEmbeddingBatchRequest(
    Guid ClientId,
    IReadOnlyList<string> Inputs,
    int? ExpectedDimensions = null);

/// <summary>
///     Response returned after one embedding batch executes.
/// </summary>
public sealed record ProCursorEmbeddingBatchResponse(
    IReadOnlyList<float[]> Embeddings,
    long? PromptTokens = null,
    long? CompletionTokens = null,
    long? TotalTokens = null);

/// <summary>
///     Refresh request used when ProCursor rehydrates runtime configuration from ProPR.
/// </summary>
public sealed record ProCursorRuntimeConfigurationRefreshRequest(
    string Reason,
    string? KnownProjectionVersion = null);

/// <summary>
///     Runtime configuration projection returned by ProPR for one ProCursor source.
/// </summary>
public sealed record ProCursorRuntimeConfigurationProjectionDto(
    string ProjectionVersion,
    DateTimeOffset FetchedAt,
    ProCursorKnowledgeSourceDto Source);
