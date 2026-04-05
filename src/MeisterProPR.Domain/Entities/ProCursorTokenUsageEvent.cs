// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     One captured ProCursor-owned AI call used for token and cost reporting.
/// </summary>
public sealed class ProCursorTokenUsageEvent
{
    public ProCursorTokenUsageEvent(
        Guid id,
        Guid clientId,
        Guid proCursorSourceId,
        string sourceDisplayNameSnapshot,
        string requestId,
        DateTimeOffset occurredAtUtc,
        ProCursorTokenUsageCallType callType,
        string deploymentName,
        string modelName,
        string tokenizerName,
        long promptTokens,
        long completionTokens,
        bool tokensEstimated,
        decimal? estimatedCostUsd,
        bool costEstimated,
        Guid? aiConnectionId = null,
        Guid? indexJobId = null,
        string? resourceId = null,
        string? sourcePath = null,
        Guid? knowledgeChunkId = null,
        string? safeMetadataJson = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        if (proCursorSourceId == Guid.Empty)
        {
            throw new ArgumentException("ProCursorSourceId must not be empty.", nameof(proCursorSourceId));
        }

        if (promptTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(promptTokens));
        }

        if (completionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completionTokens));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.ProCursorSourceId = proCursorSourceId;
        this.SourceDisplayNameSnapshot = NormalizeRequired(sourceDisplayNameSnapshot, nameof(sourceDisplayNameSnapshot));
        this.IndexJobId = indexJobId;
        this.RequestId = NormalizeRequired(requestId, nameof(requestId));
        this.OccurredAtUtc = occurredAtUtc;
        this.CallType = callType;
        this.AiConnectionId = aiConnectionId;
        this.DeploymentName = NormalizeRequired(deploymentName, nameof(deploymentName));
        this.ModelName = NormalizeRequired(modelName, nameof(modelName));
        this.TokenizerName = NormalizeRequired(tokenizerName, nameof(tokenizerName));
        this.PromptTokens = promptTokens;
        this.CompletionTokens = completionTokens;
        this.TotalTokens = promptTokens + completionTokens;
        this.TokensEstimated = tokensEstimated;
        this.EstimatedCostUsd = estimatedCostUsd;
        this.CostEstimated = costEstimated;
        this.ResourceId = NormalizeOptional(resourceId);
        this.SourcePath = NormalizeOptional(sourcePath);
        this.KnowledgeChunkId = knowledgeChunkId;
        this.SafeMetadataJson = NormalizeOptional(safeMetadataJson);
        this.CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    private ProCursorTokenUsageEvent()
    {
    }

    public Guid Id { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid ProCursorSourceId { get; private set; }

    public string SourceDisplayNameSnapshot { get; private set; } = string.Empty;

    public Guid? IndexJobId { get; private set; }

    public string RequestId { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    public ProCursorTokenUsageCallType CallType { get; private set; }

    public Guid? AiConnectionId { get; private set; }

    public string DeploymentName { get; private set; } = string.Empty;

    public string ModelName { get; private set; } = string.Empty;

    public string TokenizerName { get; private set; } = string.Empty;

    public long PromptTokens { get; private set; }

    public long CompletionTokens { get; private set; }

    public long TotalTokens { get; private set; }

    public bool TokensEstimated { get; private set; }

    public decimal? EstimatedCostUsd { get; private set; }

    public bool CostEstimated { get; private set; }

    public string? ResourceId { get; private set; }

    public string? SourcePath { get; private set; }

    public Guid? KnowledgeChunkId { get; private set; }

    public string? SafeMetadataJson { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
