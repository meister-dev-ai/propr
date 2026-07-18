// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IProtocolRecorder" />.
///     Each write uses a short-lived <see cref="MeisterProPRDbContext" /> obtained from
///     <see cref="IDbContextFactory{TContext}" /> so events are persisted atomically without
///     interfering with the request-scoped context used by the rest of the application.
///     All methods except <see cref="BeginAsync" /> swallow exceptions and log a warning so
///     that protocol recording never disrupts a review job.
/// </summary>
public sealed class EfProtocolRecorder(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ILogger<EfProtocolRecorder> logger) : IProtocolRecorder
{
    /// <inheritdoc />
    public async Task<Guid> BeginAsync(
        Guid jobId,
        int attemptNumber,
        string? label = null,
        Guid? fileResultId = null,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        ReviewPassKind? passKind = null,
        string? reason = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
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
            PassKind = passKind?.ToString(),
            Reason = reason,
        };
        db.ReviewJobProtocols.Add(protocol);
        await db.SaveChangesAsync(ct);
        return protocol.Id;
    }

    /// <inheritdoc />
    public async Task RecordAiCallAsync(
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
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var ev = new ProtocolEvent
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
                EventCategory = TraceSearchSupport.DeriveEventCategory(ProtocolEventKind.AiCall, name ?? $"ai_call_iter_{iteration}"),
                Error = Sanitize(error),
            };
            db.ProtocolEvents.Add(ev);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record AI call event for protocol {ProtocolId}", protocolId);
        }
    }

    /// <inheritdoc />
    public async Task RecordPromptStageEvidenceAsync(
        Guid protocolId,
        string stageKey,
        string variantName,
        PromptCompositionMode compositionMode,
        bool usedDefaultConstruction,
        string? systemPromptText,
        string? userPromptText,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var ev = new ProtocolEvent
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
                EventCategory = TraceSearchSupport.DeriveEventCategory(ProtocolEventKind.Operational, ReviewProtocolEventNames.PromptStageEvidenceRecorded),
            };
            db.ProtocolEvents.Add(ev);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record prompt stage evidence for protocol {ProtocolId} stage {StageKey}", protocolId, stageKey);
        }
    }

    /// <inheritdoc />
    public async Task RecordToolCallAsync(
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
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var sample = $"args={arguments}";
            var ev = new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = ProtocolEventKind.ToolCall,
                Name = toolName,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize(sample),
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
                EventCategory = TraceSearchSupport.DeriveEventCategory(ProtocolEventKind.ToolCall, toolName),
            };
            db.ProtocolEvents.Add(ev);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to record tool call event for protocol {ProtocolId} tool {ToolName}",
                protocolId,
                toolName);
        }
    }

    /// <inheritdoc />
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
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var protocol = await db.ReviewJobProtocols.FindAsync([protocolId], ct);
            if (protocol is null)
            {
                return;
            }

            protocol.CompletedAt = DateTimeOffset.UtcNow;
            protocol.Outcome = outcome;
            protocol.TotalInputTokens = (protocol.TotalInputTokens ?? 0) + totalInputTokens;
            protocol.TotalOutputTokens = (protocol.TotalOutputTokens ?? 0) + totalOutputTokens;
            protocol.TotalCachedInputTokens = totalCachedInputTokens.HasValue
                ? (protocol.TotalCachedInputTokens ?? 0) + totalCachedInputTokens.Value
                : protocol.TotalCachedInputTokens;
            protocol.TotalCacheWriteTokens = totalCacheWriteTokens.HasValue
                ? (protocol.TotalCacheWriteTokens ?? 0) + totalCacheWriteTokens.Value
                : protocol.TotalCacheWriteTokens;
            protocol.TotalReasoningTokens = totalReasoningTokens.HasValue
                ? (protocol.TotalReasoningTokens ?? 0) + totalReasoningTokens.Value
                : protocol.TotalReasoningTokens;
            protocol.CacheObservability = cacheObservability;
            protocol.IterationCount = iterationCount;
            protocol.ToolCallCount = toolCallCount;
            protocol.FinalConfidence = finalConfidence;
            await db.SaveChangesAsync(ct);

            var job = await db.ReviewJobs.FindAsync([protocol.JobId], ct);
            if (job is not null)
            {
                // Always accumulate into breakdown, using Default category if none specified
                var category = protocol.AiConnectionCategory ?? AiConnectionModelCategory.Default;
                var modelId = protocol.ModelId ?? "(default)";
                var cachedInputTokens = totalCachedInputTokens ?? 0;
                var cacheWriteTokens = totalCacheWriteTokens ?? 0;
                var reasoningTokens = totalReasoningTokens ?? 0;
                job.AccumulateTierTokens(
                    category,
                    modelId,
                    totalInputTokens,
                    totalOutputTokens,
                    cachedInputTokens,
                    cacheWriteTokens,
                    reasoningTokens);

                await db.SaveChangesAsync(ct);

                // Upsert daily token usage aggregate for the client owning this job.
                if (totalInputTokens > 0
                    || totalOutputTokens > 0
                    || cachedInputTokens > 0
                    || cacheWriteTokens > 0
                    || reasoningTokens > 0)
                {
                    var usageRepo = new ClientTokenUsageRepository(db);
                    await usageRepo.UpsertAsync(
                        job.ClientId,
                        modelId,
                        DateOnly.FromDateTime(DateTime.UtcNow),
                        totalInputTokens,
                        totalOutputTokens,
                        ct,
                        cachedInputTokens,
                        cacheWriteTokens,
                        reasoningTokens);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set completed state for protocol {ProtocolId}", protocolId);
        }
    }

    /// <inheritdoc />
    public async Task AddTokensAsync(
        Guid protocolId,
        long inputTokens,
        long outputTokens,
        AiConnectionModelCategory? connectionCategory = null,
        string? modelId = null,
        CancellationToken ct = default,
        long cachedInputTokens = 0,
        long cacheWriteTokens = 0,
        long reasoningTokens = 0)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var protocol = await db.ReviewJobProtocols.FindAsync([protocolId], ct);
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

            await db.SaveChangesAsync(ct);

            var job = await db.ReviewJobs.FindAsync([protocol.JobId], ct);
            if (job is not null)
            {
                // Always accumulate into breakdown, using provided category or Default if none
                var category = connectionCategory ?? AiConnectionModelCategory.Default;
                var effectiveModelId = modelId ?? "(default)";
                job.AccumulateTierTokens(
                    category,
                    effectiveModelId,
                    inputTokens,
                    outputTokens,
                    cachedInputTokens,
                    cacheWriteTokens,
                    reasoningTokens);

                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add tokens for protocol {ProtocolId}", protocolId);
        }
    }

    /// <inheritdoc />
    public async Task RecordMemoryEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.MemoryOperation, eventName, details, null, error, ct, "memory");
    }

    /// <inheritdoc />
    public async Task RecordDedupEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, null, error, ct, "duplicate-suppression");
    }

    /// <inheritdoc />
    public async Task RecordCommentRelevanceEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "comment-relevance");
    }

    /// <inheritdoc />
    public async Task RecordReviewFindingGateEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "review-finding-gate");
    }

    /// <inheritdoc />
    public async Task RecordVerificationEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "verification");
    }

    /// <inheritdoc />
    public async Task RecordReviewStrategyEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "review-strategy");
    }

    /// <inheritdoc />
    public async Task RecordPrWideStageEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "pr-wide-review");
    }

    /// <inheritdoc />
    public async Task RecordProRvEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "prorv-prefilter");
    }

    private async Task RecordEventAsync(
        Guid protocolId,
        ProtocolEventKind kind,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct,
        string eventCategory)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var ev = new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = kind,
                Name = eventName,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize(details),
                OutputSummary = Sanitize(output),
                EventCategory = TraceSearchSupport.NormalizeEventCategory(eventCategory),
                Error = Sanitize(error),
            };
            db.ProtocolEvents.Add(ev);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to record {EventCategory} event {EventName} for protocol {ProtocolId}",
                eventCategory,
                eventName,
                protocolId);
        }
    }

    /// <summary>
    ///     Removes null bytes rejected by PostgreSQL UTF-8.
    /// </summary>
    private static string? Sanitize(string? text)
    {
        if (text is null)
        {
            return null;
        }

        if (text.Contains('\0'))
        {
            text = text.Replace("\0", string.Empty);
        }

        return text;
    }
}
