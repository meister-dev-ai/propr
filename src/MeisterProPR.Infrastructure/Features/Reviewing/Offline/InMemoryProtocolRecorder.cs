// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Services;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory <see cref="IProtocolRecorder" /> used by offline review execution.
/// </summary>
public sealed class InMemoryProtocolRecorder(
    InMemoryReviewJobRepository jobs,
    IModelPricingResolver? pricingResolver = null) : IProtocolRecorder
{
    public Task<Guid> BeginAsync(
        Guid jobId,
        int attemptNumber,
        string? label = null,
        Guid? fileResultId = null,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        ReviewPassKind? passKind = null,
        string? reason = null,
        string? logicalModelName = null)
    {
        var job = jobs.GetById(jobId) ?? throw new InvalidOperationException($"Review job {jobId} was not found.");
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            AttemptNumber = attemptNumber,
            Label = label,
            FileResultId = fileResultId,
            StartedAt = DateTimeOffset.UtcNow,
            AiConnectionCategory = connectionCategory,
            ModelId = modelId,
            LogicalModelName = logicalModelName,
            PassKind = passKind?.ToString(),
            Reason = reason,
        };

        job.Protocols.Add(protocol);
        return Task.FromResult(protocol.Id);
    }

    public Task RecordAiCallAsync(
        Guid protocolId,
        int iteration,
        long? inputTokens,
        long? outputTokens,
        string? inputTextSample,
        string? systemPrompt,
        string? outputTextSample,
        CancellationToken ct = default,
        string? name = null,
        string? error = null,
        long? cachedInputTokens = null,
        CacheCallStatus cacheStatus = CacheCallStatus.NotApplicable,
        string? cacheMissCategory = null,
        PrefixEligibilityStatus prefixEligibility = PrefixEligibilityStatus.NotApplicable,
        string? finalizationAttemptKind = null,
        string? finalizationReason = null,
        string? finalizationOutcome = null,
        long? cacheWriteTokens = null,
        long? reasoningTokens = null)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = ProtocolEventKind.AiCall,
                Name = name ?? $"ai_call_iter_{iteration}",
                OccurredAt = DateTimeOffset.UtcNow,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CachedInputTokens = cachedInputTokens,
                CacheWriteTokens = cacheWriteTokens,
                ReasoningTokens = reasoningTokens,
                CacheStatus = cacheStatus,
                CacheMissCategory = Sanitize(cacheMissCategory),
                PrefixEligibility = prefixEligibility,
                FinalizationAttemptKind = Sanitize(finalizationAttemptKind),
                FinalizationReason = Sanitize(finalizationReason),
                FinalizationOutcome = Sanitize(finalizationOutcome),
                InputTextSample = Sanitize(inputTextSample),
                SystemPrompt = Sanitize(systemPrompt),
                OutputSummary = Sanitize(outputTextSample),
                Error = Sanitize(error),
            });

        return Task.CompletedTask;
    }

    public Task RecordPromptStageEvidenceAsync(
        Guid protocolId,
        string stageKey,
        string variantName,
        PromptCompositionMode compositionMode,
        bool usedDefaultConstruction,
        string? systemPromptText,
        string? userPromptText,
        CancellationToken ct = default)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.PromptStageEvidenceRecorded,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize(userPromptText),
                SystemPrompt = Sanitize(systemPromptText),
                OutputSummary = Sanitize(
                    JsonSerializer.Serialize(
                        new
                        {
                            stageKey,
                            variantName,
                            compositionMode = compositionMode.ToString().ToLowerInvariant(),
                            usedDefaultConstruction,
                        })),
            });

        return Task.CompletedTask;
    }

    public Task RecordToolCallAsync(
        Guid protocolId,
        string toolName,
        string arguments,
        string result,
        int iteration,
        CancellationToken ct = default,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        long? durationMs = null,
        long? waitDurationMs = null,
        long? activeDurationMs = null,
        string? timingAvailability = null,
        string? toolOutcome = null,
        IReadOnlyList<ProtocolEventPhaseTiming>? phaseTimings = null,
        string? toolEvidenceAction = null,
        int? toolEvidenceOriginalPayloadTokens = null,
        int? toolEvidenceBoundedPayloadTokens = null,
        bool? toolEvidenceRefreshable = null)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = ProtocolEventKind.ToolCall,
                Name = toolName,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize($"args={arguments}"),
                StartedAt = startedAt,
                CompletedAt = completedAt,
                DurationMs = durationMs,
                WaitDurationMs = waitDurationMs,
                ActiveDurationMs = activeDurationMs,
                TimingAvailability = Sanitize(timingAvailability),
                ToolOutcome = Sanitize(toolOutcome),
                PhaseTimings = phaseTimings?.ToList(),
                OutputSummary = Sanitize(result),
                ToolEvidenceAction = Sanitize(toolEvidenceAction),
                ToolEvidenceSourceToolName = toolEvidenceAction is null ? null : toolName,
                ToolEvidenceOriginalPayloadTokens = toolEvidenceOriginalPayloadTokens,
                ToolEvidenceBoundedPayloadTokens = toolEvidenceBoundedPayloadTokens,
                ToolEvidenceRefreshable = toolEvidenceRefreshable,
            });

        return Task.CompletedTask;
    }

    public async Task SetCompletedAsync(
        Guid protocolId,
        string outcome,
        long totalInputTokens,
        long totalOutputTokens,
        int iterationCount,
        int toolCallCount,
        int? finalConfidence,
        CancellationToken ct = default,
        long? totalCachedInputTokens = null,
        CacheObservabilityStatus cacheObservability = CacheObservabilityStatus.Unknown,
        long? totalCacheWriteTokens = null,
        long? totalReasoningTokens = null)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return;
        }

        protocol.CompletedAt = DateTimeOffset.UtcNow;
        protocol.Outcome = outcome;
        protocol.TotalInputTokens = totalInputTokens;
        protocol.TotalOutputTokens = totalOutputTokens;
        protocol.TotalCachedInputTokens = totalCachedInputTokens;
        protocol.TotalCacheWriteTokens = totalCacheWriteTokens;
        protocol.TotalReasoningTokens = totalReasoningTokens;
        protocol.CacheObservability = cacheObservability;
        protocol.IterationCount = iterationCount;
        protocol.ToolCallCount = toolCallCount;
        protocol.FinalConfidence = finalConfidence;

        var job = jobs.GetById(protocol.JobId);
        if (job is not null)
        {
            var category = protocol.AiConnectionCategory ?? AiConnectionModelCategory.Default;
            var modelId = protocol.ModelId ?? "(default)";
            var logicalModelName = protocol.LogicalModelName;
            job.AccumulateTierTokens(
                category,
                modelId,
                totalInputTokens,
                totalOutputTokens,
                totalCachedInputTokens ?? 0,
                totalCacheWriteTokens ?? 0,
                totalReasoningTokens ?? 0,
                logicalModelName);
            await this.ApplyTierCostAsync(job, category, modelId, ct, logicalModelName);
        }
    }

    public async Task AddTokensAsync(
        Guid protocolId,
        long inputTokens,
        long outputTokens,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        long cachedInputTokens = 0,
        long cacheWriteTokens = 0,
        long reasoningTokens = 0,
        string? logicalModelName = null)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return;
        }

        protocol.TotalInputTokens = (protocol.TotalInputTokens ?? 0) + inputTokens;
        protocol.TotalOutputTokens = (protocol.TotalOutputTokens ?? 0) + outputTokens;
        if (cachedInputTokens > 0)
        {
            protocol.TotalCachedInputTokens = (protocol.TotalCachedInputTokens ?? 0) + cachedInputTokens;
        }

        if (cacheWriteTokens > 0)
        {
            protocol.TotalCacheWriteTokens = (protocol.TotalCacheWriteTokens ?? 0) + cacheWriteTokens;
        }

        if (reasoningTokens > 0)
        {
            protocol.TotalReasoningTokens = (protocol.TotalReasoningTokens ?? 0) + reasoningTokens;
        }

        var job = jobs.GetById(protocol.JobId);
        if (job is not null)
        {
            var category = connectionCategory ?? AiConnectionModelCategory.Default;
            var effectiveModelId = modelId ?? protocol.ModelId ?? "(default)";
            // Prefer the caller's logical model; fall back to the pass's when the caller reused the pass runtime.
            var effectiveLogicalModelName = logicalModelName ?? protocol.LogicalModelName;
            job.AccumulateTierTokens(
                category,
                effectiveModelId,
                inputTokens,
                outputTokens,
                cachedInputTokens,
                cacheWriteTokens,
                reasoningTokens,
                effectiveLogicalModelName);
            await this.ApplyTierCostAsync(job, category, effectiveModelId, ct, effectiveLogicalModelName);
        }
    }

    /// <summary>
    ///     Best-effort tier-cost computation for the offline recorder. Resolves the model's pricing (when a
    ///     resolver is configured) and recomputes the tier's cumulative cost onto the job. Any failure is
    ///     swallowed so cost never breaks offline token recording.
    /// </summary>
    private async Task ApplyTierCostAsync(
        ReviewJob job,
        AiConnectionModelCategory category,
        string modelId,
        CancellationToken ct,
        string? logicalModelName = null)
    {
        if (pricingResolver is null)
        {
            return;
        }

        try
        {
            var pricing = await pricingResolver.ResolveAsync(job.AiConnectionId ?? Guid.Empty, category, modelId, ct)
                          ?? new ModelPricing(null, null);

            var entry = job.TokenBreakdown.FirstOrDefault(candidate =>
                candidate.ConnectionCategory == category &&
                string.Equals(candidate.ModelId, modelId, StringComparison.Ordinal) &&
                string.Equals(candidate.LogicalModelName, logicalModelName, StringComparison.Ordinal));

            if (entry is not null)
            {
                var cost = AiCostCalculator.Calculate(
                    new AiTokenUsage(
                        entry.TotalInputTokens,
                        entry.TotalOutputTokens,
                        entry.TotalCachedInputTokens,
                        entry.TotalCacheWriteTokens,
                        entry.TotalReasoningTokens),
                    pricing);
                job.SetTierCost(category, modelId, cost.Usd, cost.IsApproximate, logicalModelName);
            }
        }
        catch (Exception)
        {
            // Offline cost is best-effort; a resolver failure must never break token recording.
        }
    }

    public Task RecordMemoryEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.MemoryOperation, eventName, details, null, error);
    }

    public Task RecordDedupEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, null, error);
    }

    public Task RecordPublicationEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, null, error);
    }

    public Task RecordCommentRelevanceEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    public Task RecordReviewFindingGateEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    public Task RecordVerificationEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    public Task RecordReviewStrategyEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    public Task RecordPrWideStageEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    public Task RecordProRvEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    public Task RecordLogicalModelResolutionEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        return this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error);
    }

    private Task RecordEventAsync(
        Guid protocolId,
        ProtocolEventKind kind,
        string eventName,
        string? details,
        string? output,
        string? error)
    {
        var protocol = this.FindProtocol(protocolId);
        if (protocol is null)
        {
            return Task.CompletedTask;
        }

        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = kind,
                Name = eventName,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize(details),
                OutputSummary = Sanitize(output),
                Error = Sanitize(error),
            });

        return Task.CompletedTask;
    }

    private ReviewJobProtocol? FindProtocol(Guid protocolId)
    {
        return jobs.GetAllJobsAsync(int.MaxValue, 0, null).Result.items
            .SelectMany(job => job.Protocols)
            .FirstOrDefault(protocol => protocol.Id == protocolId);
    }

    private static string? Sanitize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        return text.Replace("\0", string.Empty, StringComparison.Ordinal);
    }
}
