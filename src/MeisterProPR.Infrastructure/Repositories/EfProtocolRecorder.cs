// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
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
        CancellationToken ct = default)
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
        string? outputTextSample,
        CancellationToken ct = default,
        string? name = null)
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
                InputTextSample = Sanitize(inputTextSample),
                OutputSummary = Sanitize(outputTextSample),
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
    public async Task RecordToolCallAsync(
        Guid protocolId,
        string toolName,
        string arguments,
        string result,
        int iteration,
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var sample = $"args={arguments}";
            var effectiveResult = iteration > 3 && !string.IsNullOrEmpty(result) &&
                                  result.Length > ProtocolLimits.ToolResultExcerptMaxLength
                ? string.Concat(result.AsSpan(0, ProtocolLimits.ToolResultExcerptMaxLength), " [TRUNCATED]")
                : result;
            var ev = new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = ProtocolEventKind.ToolCall,
                Name = toolName,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize(sample),
                OutputSummary = Sanitize(effectiveResult),
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
        CancellationToken ct = default)
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
            protocol.TotalInputTokens = totalInputTokens;
            protocol.TotalOutputTokens = totalOutputTokens;
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
                job.AccumulateTierTokens(category, modelId, totalInputTokens, totalOutputTokens);

                await db.SaveChangesAsync(ct);

                // Upsert daily token usage aggregate for the client owning this job.
                if (totalInputTokens > 0 || totalOutputTokens > 0)
                {
                    var usageRepo = new ClientTokenUsageRepository(db);
                    await usageRepo.UpsertAsync(
                        job.ClientId,
                        modelId,
                        DateOnly.FromDateTime(DateTime.UtcNow),
                        totalInputTokens,
                        totalOutputTokens,
                        ct);
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
        CancellationToken ct = default)
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
            await db.SaveChangesAsync(ct);

            var job = await db.ReviewJobs.FindAsync([protocol.JobId], ct);
            if (job is not null)
            {
                // Always accumulate into breakdown, using provided category or Default if none
                var category = connectionCategory ?? AiConnectionModelCategory.Default;
                var effectiveModelId = modelId ?? "(default)";
                job.AccumulateTierTokens(category, effectiveModelId, inputTokens, outputTokens);

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
        await this.RecordOperationalEventAsync(protocolId, eventName, details, error, ct, "memory");
    }

    /// <inheritdoc />
    public async Task RecordDedupEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
        string? error,
        CancellationToken ct = default)
    {
        await this.RecordOperationalEventAsync(protocolId, eventName, details, error, ct, "duplicate-suppression");
    }

    private async Task RecordOperationalEventAsync(
        Guid protocolId,
        string eventName,
        string? details,
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
                Kind = ProtocolEventKind.MemoryOperation,
                Name = eventName,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = Sanitize(details),
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
    ///     Removes null bytes (rejected by PostgreSQL UTF-8) and truncates to
    ///     <see cref="ProtocolLimits.TextSampleMaxLength" />.
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

        return text.Length > ProtocolLimits.TextSampleMaxLength ? text[..ProtocolLimits.TextSampleMaxLength] : text;
    }
}
