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
    public async Task<Guid> BeginAsync(Guid jobId, int attemptNumber, string? label = null, Guid? fileResultId = null, CancellationToken ct = default)
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
        CancellationToken ct = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var ev = new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocolId,
                Kind = ProtocolEventKind.AiCall,
                Name = $"ai_call_iter_{iteration}",
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
        CancellationToken ct = default)
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
                OutputSummary = Sanitize(result),
            };
            db.ProtocolEvents.Add(ev);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record tool call event for protocol {ProtocolId} tool {ToolName}", protocolId, toolName);
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
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to set completed state for protocol {ProtocolId}", protocolId);
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
