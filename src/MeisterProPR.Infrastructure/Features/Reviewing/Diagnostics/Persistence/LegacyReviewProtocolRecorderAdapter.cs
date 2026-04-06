// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

/// <summary>
///     Adapts the legacy protocol recorder onto the Reviewing-owned recorder boundary.
/// </summary>
public sealed class LegacyReviewProtocolRecorderAdapter(IProtocolRecorder inner) : IReviewProtocolRecorder
{
    public Task<Guid> BeginAsync(Guid jobId, int attemptNumber, string? label = null, Guid? fileResultId = null, AiConnectionModelCategory? connectionCategory = null, string? modelId = null, CancellationToken ct = default)
        => inner.BeginAsync(jobId, attemptNumber, label, fileResultId, connectionCategory, modelId, ct);

    public Task RecordAiCallAsync(Guid protocolId, int iteration, long? inputTokens, long? outputTokens, string? inputTextSample, string? outputTextSample, CancellationToken ct = default, string? name = null)
        => inner.RecordAiCallAsync(protocolId, iteration, inputTokens, outputTokens, inputTextSample, outputTextSample, ct, name);

    public Task RecordToolCallAsync(Guid protocolId, string toolName, string arguments, string result, int iteration, CancellationToken ct = default)
        => inner.RecordToolCallAsync(protocolId, toolName, arguments, result, iteration, ct);

    public Task SetCompletedAsync(Guid protocolId, string outcome, long totalInputTokens, long totalOutputTokens, int iterationCount, int toolCallCount, int? finalConfidence, CancellationToken ct = default)
        => inner.SetCompletedAsync(protocolId, outcome, totalInputTokens, totalOutputTokens, iterationCount, toolCallCount, finalConfidence, ct);

    public Task AddTokensAsync(Guid protocolId, long inputTokens, long outputTokens, AiConnectionModelCategory? connectionCategory = null, string? modelId = null, CancellationToken ct = default)
        => inner.AddTokensAsync(protocolId, inputTokens, outputTokens, connectionCategory, modelId, ct);

    public Task RecordMemoryEventAsync(Guid protocolId, string eventName, string? details, string? error, CancellationToken ct = default)
        => inner.RecordMemoryEventAsync(protocolId, eventName, details, error, ct);

    public Task RecordDedupEventAsync(Guid protocolId, string eventName, string? details, string? error, CancellationToken ct = default)
        => inner.RecordDedupEventAsync(protocolId, eventName, details, error, ct);
}
