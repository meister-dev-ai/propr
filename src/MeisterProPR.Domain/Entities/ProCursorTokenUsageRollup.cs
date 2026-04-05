// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Pre-aggregated daily or monthly ProCursor usage bucket used to accelerate reporting queries.
/// </summary>
public sealed class ProCursorTokenUsageRollup
{
    public ProCursorTokenUsageRollup(
        Guid id,
        Guid clientId,
        Guid? proCursorSourceId,
        string? sourceDisplayNameSnapshot,
        DateOnly bucketStartDate,
        ProCursorTokenUsageGranularity granularity,
        string modelName,
        long promptTokens,
        long completionTokens,
        decimal? estimatedCostUsd,
        long eventCount,
        long estimatedEventCount,
        DateTimeOffset lastRecomputedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        if (promptTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(promptTokens));
        }

        if (completionTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completionTokens));
        }

        if (eventCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eventCount));
        }

        if (estimatedEventCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(estimatedEventCount));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.ProCursorSourceId = proCursorSourceId;
        this.SourceDisplayNameSnapshot = NormalizeOptional(sourceDisplayNameSnapshot);
        this.BucketStartDate = bucketStartDate;
        this.Granularity = granularity;
        this.ModelName = NormalizeRequired(modelName, nameof(modelName));
        this.PromptTokens = promptTokens;
        this.CompletionTokens = completionTokens;
        this.TotalTokens = promptTokens + completionTokens;
        this.EstimatedCostUsd = estimatedCostUsd;
        this.EventCount = eventCount;
        this.EstimatedEventCount = estimatedEventCount;
        this.LastRecomputedAtUtc = lastRecomputedAtUtc;
    }

    private ProCursorTokenUsageRollup()
    {
    }

    public Guid Id { get; private set; }

    public Guid ClientId { get; private set; }

    public Guid? ProCursorSourceId { get; private set; }

    public string? SourceDisplayNameSnapshot { get; private set; }

    public DateOnly BucketStartDate { get; private set; }

    public ProCursorTokenUsageGranularity Granularity { get; private set; }

    public string ModelName { get; private set; } = string.Empty;

    public long PromptTokens { get; private set; }

    public long CompletionTokens { get; private set; }

    public long TotalTokens { get; private set; }

    public decimal? EstimatedCostUsd { get; private set; }

    public long EventCount { get; private set; }

    public long EstimatedEventCount { get; private set; }

    public DateTimeOffset LastRecomputedAtUtc { get; private set; }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
