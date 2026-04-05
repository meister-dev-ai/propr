// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IProCursorTokenUsageRecorder"/>.
///     Uses short-lived contexts so capture does not interfere with the primary ProCursor workflow.
/// </summary>
public sealed partial class EfProCursorTokenUsageRecorder(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ILogger<EfProCursorTokenUsageRecorder> logger) : IProCursorTokenUsageRecorder
{
    /// <inheritdoc />
    public async Task RecordAsync(ProCursorTokenUsageCaptureRequest request, CancellationToken ct = default)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var exists = await db.ProCursorTokenUsageEvents.AnyAsync(
                candidate => candidate.ClientId == request.ClientId && candidate.RequestId == request.RequestId,
                ct);

            if (exists)
            {
                LogDuplicateIgnored(logger, request.ClientId, request.RequestId);
                return;
            }

            db.ProCursorTokenUsageEvents.Add(new ProCursorTokenUsageEvent(
                Guid.NewGuid(),
                request.ClientId,
                request.ProCursorSourceId,
                request.SourceDisplayNameSnapshot,
                request.RequestId,
                request.OccurredAtUtc,
                request.CallType,
                request.DeploymentName,
                request.ModelName,
                request.TokenizerName,
                request.PromptTokens,
                request.CompletionTokens,
                request.TokensEstimated,
                request.EstimatedCostUsd,
                request.CostEstimated,
                request.AiConnectionId,
                request.IndexJobId,
                request.ResourceId,
                request.SourcePath,
                request.KnowledgeChunkId,
                Sanitize(request.SafeMetadataJson)));

            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateRequestIdViolation(ex))
        {
            LogDuplicateCommittedIgnored(logger, request.ClientId, request.RequestId);
        }
        catch (Exception ex)
        {
            LogRecordFailed(logger, ex, request.ClientId, request.RequestId);
        }
    }

    private static string? Sanitize(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Contains('\0', StringComparison.Ordinal)
            ? value.Replace("\0", string.Empty, StringComparison.Ordinal)
            : value;
    }

    private static bool IsDuplicateRequestIdViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "ux_procursor_token_usage_events_client_request",
        };
}
