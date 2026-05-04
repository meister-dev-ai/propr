// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     One captured ProCursor-owned AI call used for token and cost reporting.
/// </summary>
public sealed class ProCursorTokenUsageEvent
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorTokenUsageEvent" /> class.
    /// </summary>
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
        this.SourceDisplayNameSnapshot = NormalizeRequired(
            sourceDisplayNameSnapshot,
            nameof(sourceDisplayNameSnapshot));
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

    /// <summary>
    ///     Gets the unique identifier for this event.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the client ID associated with this event.
    /// </summary>
    public Guid ClientId { get; private set; }

    /// <summary>
    ///     Gets the ProCursor source ID for this event.
    /// </summary>
    public Guid ProCursorSourceId { get; private set; }

    /// <summary>
    ///     Gets the snapshot of the source display name.
    /// </summary>
    public string SourceDisplayNameSnapshot { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the optional index job ID associated with this event.
    /// </summary>
    public Guid? IndexJobId { get; private set; }

    /// <summary>
    ///     Gets the request ID for this event.
    /// </summary>
    public string RequestId { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the UTC timestamp when this event occurred.
    /// </summary>
    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>
    ///     Gets the type of AI call this event represents.
    /// </summary>
    public ProCursorTokenUsageCallType CallType { get; private set; }

    /// <summary>
    ///     Gets the optional AI connection ID for this event.
    /// </summary>
    public Guid? AiConnectionId { get; private set; }

    /// <summary>
    ///     Gets the deployment name used for this call.
    /// </summary>
    public string DeploymentName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the model name used for this call.
    /// </summary>
    public string ModelName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the tokenizer name used for this call.
    /// </summary>
    public string TokenizerName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the number of prompt tokens used.
    /// </summary>
    public long PromptTokens { get; private set; }

    /// <summary>
    ///     Gets the number of completion tokens used.
    /// </summary>
    public long CompletionTokens { get; private set; }

    /// <summary>
    ///     Gets the total number of tokens used (prompt + completion).
    /// </summary>
    public long TotalTokens { get; private set; }

    /// <summary>
    ///     Gets a value indicating whether the token counts were estimated.
    /// </summary>
    public bool TokensEstimated { get; private set; }

    /// <summary>
    ///     Gets the estimated cost in USD for this event.
    /// </summary>
    public decimal? EstimatedCostUsd { get; private set; }

    /// <summary>
    ///     Gets a value indicating whether the cost was estimated.
    /// </summary>
    public bool CostEstimated { get; private set; }

    /// <summary>
    ///     Gets the optional resource ID associated with this event.
    /// </summary>
    public string? ResourceId { get; private set; }

    /// <summary>
    ///     Gets the optional source path associated with this event.
    /// </summary>
    public string? SourcePath { get; private set; }

    /// <summary>
    ///     Gets the optional knowledge chunk ID associated with this event.
    /// </summary>
    public Guid? KnowledgeChunkId { get; private set; }

    /// <summary>
    ///     Gets the optional safe metadata JSON associated with this event.
    /// </summary>
    public string? SafeMetadataJson { get; private set; }

    /// <summary>
    ///     Gets the UTC timestamp when this event was created.
    /// </summary>
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
