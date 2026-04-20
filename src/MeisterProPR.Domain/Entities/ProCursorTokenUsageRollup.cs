// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Pre-aggregated daily or monthly ProCursor usage bucket used to accelerate reporting queries.
/// </summary>
public sealed class ProCursorTokenUsageRollup
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorTokenUsageRollup"/> class.
    /// </summary>
    /// <param name="id">The unique identifier for the rollup.</param>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="proCursorSourceId">The ProCursor source identifier.</param>
    /// <param name="sourceDisplayNameSnapshot">The source display name snapshot.</param>
    /// <param name="bucketStartDate">The start date of the bucket.</param>
    /// <param name="granularity">The granularity of the rollup.</param>
    /// <param name="modelName">The model name.</param>
    /// <param name="promptTokens">The number of prompt tokens.</param>
    /// <param name="completionTokens">The number of completion tokens.</param>
    /// <param name="estimatedCostUsd">The estimated cost in USD.</param>
    /// <param name="eventCount">The count of events.</param>
    /// <param name="estimatedEventCount">The estimated event count.</param>
    /// <param name="lastRecomputedAtUtc">The last recomputed time in UTC.</param>
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

    /// <summary>
    ///     Gets the unique identifier for the rollup.
    /// </summary>
    public Guid Id { get; private set; }

    /// <summary>
    ///     Gets the client identifier.
    /// </summary>
    public Guid ClientId { get; private set; }

    /// <summary>
    ///     Gets the ProCursor source identifier.
    /// </summary>
    public Guid? ProCursorSourceId { get; private set; }

    /// <summary>
    ///     Gets the source display name snapshot.
    /// </summary>
    public string? SourceDisplayNameSnapshot { get; private set; }

    /// <summary>
    ///     Gets the start date of the bucket.
    /// </summary>
    public DateOnly BucketStartDate { get; private set; }

    /// <summary>
    ///     Gets the granularity of the rollup.
    /// </summary>
    public ProCursorTokenUsageGranularity Granularity { get; private set; }

    /// <summary>
    ///     Gets the model name.
    /// </summary>
    public string ModelName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the number of prompt tokens.
    /// </summary>
    public long PromptTokens { get; private set; }

    /// <summary>
    ///     Gets the number of completion tokens.
    /// </summary>
    public long CompletionTokens { get; private set; }

    /// <summary>
    ///     Gets the total number of tokens.
    /// </summary>
    public long TotalTokens { get; private set; }

    /// <summary>
    ///     Gets the estimated cost in USD.
    /// </summary>
    public decimal? EstimatedCostUsd { get; private set; }

    /// <summary>
    ///     Gets the count of events.
    /// </summary>
    public long EventCount { get; private set; }

    /// <summary>
    ///     Gets the estimated event count.
    /// </summary>
    public long EstimatedEventCount { get; private set; }

    /// <summary>
    ///     Gets the last recomputed time in UTC.
    /// </summary>
    public DateTimeOffset LastRecomputedAtUtc { get; private set; }

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
