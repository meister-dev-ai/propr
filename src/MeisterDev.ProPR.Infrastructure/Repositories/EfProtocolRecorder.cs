// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

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
    ILogger<EfProtocolRecorder> logger,
    IModelPricingResolver? pricingResolver = null) : IProtocolRecorder
{
    /// <summary>
    ///     Stands in for a model the call never named, once the job's own model has been tried as well. Tokens
    ///     recorded under it cannot be priced, because nothing identifies the rate they were bought at, so this
    ///     name appearing in a breakdown means real spend went uncounted rather than that it was free.
    /// </summary>
    private const string UnknownModelId = "(default)";

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
        string? reason = null,
        string? logicalModelName = null)
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
            LogicalModelName = logicalModelName,
            PassKind = passKind?.ToString(),
            Reason = reason,
        };
        db.ReviewJobProtocols.Add(protocol);
        await db.SaveChangesAsync(ct);
        return protocol.Id;
    }

    /// <inheritdoc />
    public async Task<Guid> BeginForThreadPassAsync(
        Guid threadPassJobId,
        int attemptNumber,
        string? label = null,
        string? modelId = null,
        CancellationToken ct = default,
        string? logicalModelName = null)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            ThreadPassJobId = threadPassJobId,
            AttemptNumber = attemptNumber,
            Label = label,
            StartedAt = DateTimeOffset.UtcNow,
            ModelId = modelId,
            LogicalModelName = logicalModelName,
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

            var totals = new ProtocolTotals(
                totalInputTokens,
                totalOutputTokens,
                totalCachedInputTokens ?? 0,
                totalCacheWriteTokens ?? 0,
                totalReasoningTokens ?? 0);

            if (protocol.JobId is { } reviewJobId)
            {
                await this.PropagateToReviewJobAsync(db, protocol, reviewJobId, totals, ct);
            }
            else if (protocol.ThreadPassJobId is { } threadPassJobId)
            {
                await this.PropagateToThreadPassAsync(db, protocol, threadPassJobId, totals, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set completed state for protocol {ProtocolId}", protocolId);
        }
    }

    private async Task PropagateToReviewJobAsync(
        MeisterProPRDbContext db,
        ReviewJobProtocol protocol,
        Guid reviewJobId,
        ProtocolTotals totals,
        CancellationToken ct)
    {
        var job = await db.ReviewJobs.FindAsync([reviewJobId], ct);
        if (job is null)
        {
            return;
        }

        // Always accumulate into breakdown, using Default category if none specified
        var category = protocol.AiConnectionCategory ?? AiConnectionModelCategory.Default;
        var modelId = protocol.ModelId ?? job.AiModel ?? UnknownModelId;
        var modelInferred = protocol.ModelId is null && job.AiModel is not null;
        var logicalModelName = protocol.LogicalModelName;
        job.AccumulateTierTokens(
            category,
            modelId,
            totals.InputTokens,
            totals.OutputTokens,
            totals.CachedInputTokens,
            totals.CacheWriteTokens,
            totals.ReasoningTokens,
            logicalModelName);

        await db.SaveChangesAsync(ct);

        // Best-effort cost: a pricing-lookup failure must never break token recording.
        var passCostDelta = await this.ApplyTierCostAsync(
            db,
            job,
            category,
            modelId,
            totals.InputTokens,
            totals.OutputTokens,
            totals.CachedInputTokens,
            totals.CacheWriteTokens,
            totals.ReasoningTokens,
            ct,
            logicalModelName,
            modelInferred);

        // Upsert daily token usage aggregate for the client owning this job.
        if (totals.AnyTokens)
        {
            var usageRepo = new ClientTokenUsageRepository(db);
            await usageRepo.UpsertAsync(
                job.ClientId,
                modelId,
                DateOnly.FromDateTime(DateTime.UtcNow),
                totals.InputTokens,
                totals.OutputTokens,
                ct,
                totals.CachedInputTokens,
                totals.CacheWriteTokens,
                totals.ReasoningTokens,
                passCostDelta,
                logicalModelName ?? string.Empty,
                await ResolveProviderKindAsync(db, job.AiConnectionId, ct));
        }
    }

    /// <summary>
    ///     Moves one closed thread-pass protocol's tokens onto the pass's own totals and the client's daily usage
    ///     sample.
    /// </summary>
    /// <remarks>
    ///     The pass carries a single total rather than a per-tier breakdown: it makes one call per thread on one
    ///     resolved runtime, so there are no effort tiers to tell apart. The daily sample is keyed exactly as the
    ///     review path keys it, which is how the client month-to-date scope reaches this spend without knowing a
    ///     thread pass exists.
    /// </remarks>
    private async Task PropagateToThreadPassAsync(
        MeisterProPRDbContext db,
        ReviewJobProtocol protocol,
        Guid threadPassJobId,
        ProtocolTotals totals,
        CancellationToken ct)
    {
        var pass = await db.ThreadPassJobs.FindAsync([threadPassJobId], ct);
        if (pass is null || !totals.AnyTokens)
        {
            return;
        }

        var modelId = protocol.ModelId ?? pass.AiModel ?? UnknownModelId;
        var pricing = await this.TryResolvePricingAsync(pass.AiConnectionId ?? Guid.Empty, modelId, ct);
        var cost = pricing is null
            ? null
            : AiCostCalculator.Calculate(
                    new AiTokenUsage(
                        totals.InputTokens,
                        totals.OutputTokens,
                        totals.CachedInputTokens,
                        totals.CacheWriteTokens,
                        totals.ReasoningTokens),
                    pricing)
                .Usd;

        pass.AccumulateSpend(totals.InputTokens, totals.OutputTokens, cost);
        await db.SaveChangesAsync(ct);

        var usageRepo = new ClientTokenUsageRepository(db);
        await usageRepo.UpsertAsync(
            pass.ClientId,
            modelId,
            DateOnly.FromDateTime(DateTime.UtcNow),
            totals.InputTokens,
            totals.OutputTokens,
            ct,
            totals.CachedInputTokens,
            totals.CacheWriteTokens,
            totals.ReasoningTokens,
            cost,
            protocol.LogicalModelName ?? string.Empty,
            await ResolveProviderKindAsync(db, pass.AiConnectionId, ct));
    }

    /// <summary>
    ///     Resolves a model's pricing, returning <see langword="null" /> when no resolver is configured or the
    ///     lookup fails. Cost is best-effort everywhere: the tokens are already spent, so a pricing failure must
    ///     leave the token record standing rather than discard it.
    /// </summary>
    private async Task<ModelPricing?> TryResolvePricingAsync(Guid connectionId, string modelId, CancellationToken ct)
    {
        if (pricingResolver is null)
        {
            return null;
        }

        try
        {
            return await pricingResolver.ResolveAsync(connectionId, AiConnectionModelCategory.Default, modelId, ct)
                   ?? new ModelPricing(null, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve pricing for model {ModelId}", modelId);
            return null;
        }
    }

    /// <summary>The token counts one closed protocol contributes to whichever unit of work owns it.</summary>
    private sealed record ProtocolTotals(
        long InputTokens,
        long OutputTokens,
        long CachedInputTokens,
        long CacheWriteTokens,
        long ReasoningTokens)
    {
        public bool AnyTokens => this.InputTokens > 0
                                 || this.OutputTokens > 0
                                 || this.CachedInputTokens > 0
                                 || this.CacheWriteTokens > 0
                                 || this.ReasoningTokens > 0;
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
        long reasoningTokens = 0,
        string? logicalModelName = null)
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

            var job = protocol.JobId is { } reviewJobId
                ? await db.ReviewJobs.FindAsync([reviewJobId], ct)
                : null;
            if (job is not null)
            {
                // Always accumulate into breakdown, using provided category or Default if none
                var category = connectionCategory ?? AiConnectionModelCategory.Default;
                var effectiveModelId = modelId ?? job.AiModel ?? UnknownModelId;
                var modelInferred = modelId is null && job.AiModel is not null;
                // NOSONAR — the caller's logical model wins, because an out-of-loop call may use a different
                // role than the pass. The pass's role is the fallback when the caller reused the pass
                // runtime without naming one.
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

                await db.SaveChangesAsync(ct);

                // Best-effort cost: a pricing-lookup failure must never break token recording.
                _ = await this.ApplyTierCostAsync(
                    db,
                    job,
                    category,
                    effectiveModelId,
                    inputTokens,
                    outputTokens,
                    cachedInputTokens,
                    cacheWriteTokens,
                    reasoningTokens,
                    ct,
                    effectiveLogicalModelName,
                    modelInferred);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add tokens for protocol {ProtocolId}", protocolId);
        }
    }

    /// <summary>
    ///     Reads the provider family behind the job's connection profile so the daily usage row can be attributed
    ///     to it. Returns the empty string when there is no connection or it has since been deleted: an
    ///     unattributed row is still worth keeping, and refusing to record usage over a missing profile would lose
    ///     the tokens entirely.
    /// </summary>
    private static async Task<string> ResolveProviderKindAsync(
        MeisterProPRDbContext db,
        Guid? connectionId,
        CancellationToken ct)
    {
        if (connectionId is null || connectionId == Guid.Empty)
        {
            return string.Empty;
        }

        var providerKind = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == connectionId)
            .Select(profile => profile.ProviderKind)
            .FirstOrDefaultAsync(ct);

        return providerKind ?? string.Empty;
    }

    /// <summary>
    ///     Resolves the model's pricing, recomputes the tier's cumulative cost onto the job's breakdown and
    ///     total, and returns the per-pass cost delta for the daily usage sample. Best-effort: token recording
    ///     has already been persisted before this runs, and any failure here is swallowed so cost never breaks
    ///     token recording. Returns <see langword="null" /> when no resolver is configured or on failure.
    /// </summary>
    private async Task<decimal?> ApplyTierCostAsync(
        MeisterProPRDbContext db,
        ReviewJob job,
        AiConnectionModelCategory category,
        string modelId,
        long passInputTokens,
        long passOutputTokens,
        long passCachedInputTokens,
        long passCacheWriteTokens,
        long passReasoningTokens,
        CancellationToken ct,
        string? logicalModelName = null,
        bool modelInferred = false)
    {
        if (pricingResolver is null)
        {
            return null;
        }

        try
        {
            // Pricing is per physical model; the logical-model name only selects which breakdown entry to price.
            var pricing = await pricingResolver.ResolveAsync(job.AiConnectionId ?? Guid.Empty, category, modelId, ct)
                          ?? new ModelPricing(null, null);

            var tierEntry = job.TokenBreakdown.FirstOrDefault(entry =>
                entry.ConnectionCategory == category &&
                string.Equals(entry.ModelId, modelId, StringComparison.Ordinal) &&
                string.Equals(entry.LogicalModelName, logicalModelName, StringComparison.Ordinal));

            if (tierEntry is not null)
            {
                var cumulative = AiCostCalculator.Calculate(
                    new AiTokenUsage(
                        tierEntry.TotalInputTokens,
                        tierEntry.TotalOutputTokens,
                        tierEntry.TotalCachedInputTokens,
                        tierEntry.TotalCacheWriteTokens,
                        tierEntry.TotalReasoningTokens),
                    pricing);
                // A cost priced against a model the call did not name is an attribution, not a measurement, so it
                // is reported as approximate even when every rate behind it was configured exactly.
                job.SetTierCost(category, modelId, cumulative.Usd, cumulative.IsApproximate || modelInferred, logicalModelName);
                await db.SaveChangesAsync(ct);
            }

            return AiCostCalculator.Calculate(
                    new AiTokenUsage(
                        passInputTokens,
                        passOutputTokens,
                        passCachedInputTokens,
                        passCacheWriteTokens,
                        passReasoningTokens),
                    pricing)
                .Usd;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute estimated cost for job {JobId}", job.Id);
            return null;
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
    public async Task RecordPublicationEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, null, error, ct, "publication");
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

    /// <inheritdoc />
    public async Task RecordLogicalModelResolutionEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? output,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordEventAsync(protocolId, ProtocolEventKind.Operational, eventName, details, output, error, ct, "logical-model-resolution");
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
