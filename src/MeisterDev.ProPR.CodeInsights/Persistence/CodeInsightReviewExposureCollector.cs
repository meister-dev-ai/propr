// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Best-effort durable capture of actual completed model execution.</summary>
public sealed partial class CodeInsightReviewExposureCollector(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightReviewExposureCollector> logger,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null,
    IServiceScopeFactory? scopeFactory = null) : ICodeInsightReviewExposureCollector
{
    private const int MaximumWriteAttempts = 3;

    public async Task RecordAsync(
        CodeInsightPullRequestKey key, Guid jobId, string filePath, string revisionKey,
        string modelId, string? logicalModelName, string providerScope, string source, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelId))
            {
                return;
            }

            await using var child = scopeFactory?.CreateAsyncScope();
            var isolatedGate = child?.ServiceProvider.GetRequiredService<ICodeInsightsCollectionGate>() ?? gate;
            if (!await isolatedGate.IsCollectionEnabledAsync(key.ClientId, ct))
            {
                return;
            }

            for (var attempt = 0;; attempt++)
            {
                try
                {
                    await this.RecordAttemptAsync(key, jobId, filePath, revisionKey, modelId, logicalModelName, providerScope, source, observedAt, ct);
                    return;
                }
                catch (DbUpdateException exception) when (attempt < MaximumWriteAttempts - 1 &&
                                                          exception.InnerException is PostgresException { SqlState: "23505" })
                {
                    if (contextFactory is null)
                    {
                        dbContext.ChangeTracker.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex);
        }
    }

    private async Task RecordAttemptAsync(
        CodeInsightPullRequestKey key, Guid jobId, string filePath, string revisionKey,
        string modelId, string? logicalModelName, string providerScope, string source, DateTimeOffset observedAt, CancellationToken ct)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var pr = await db.CodeInsightPullRequests.FirstOrDefaultAsync(
            row => row.ClientId == key.ClientId && row.RepositoryId == key.RepositoryId && row.PullRequestId == key.PullRequestId, ct);
        if (pr is null)
        {
            pr = new()
            {
                Id = Guid.CreateVersion7(),
                ClientId = key.ClientId,
                RepositoryId = key.RepositoryId,
                PullRequestId = key.PullRequestId,
                CreatedAt = observedAt,
                LastActivityAt = observedAt,
                UpdatedAt = observedAt
            };
            db.CodeInsightPullRequests.Add(pr);
        }

        var logical = logicalModelName ?? "";
        if (await db.CodeInsightReviewExposures.AnyAsync(
                row => row.JobId == jobId && row.FilePath == filePath && row.ModelId == modelId && row.LogicalModelName == logical && row.Source == source,
                ct))
        {
            return;
        }

        db.CodeInsightReviewExposures.Add(
            new()
            {
                Id = Guid.CreateVersion7(),
                CodeInsightPullRequestId = pr.Id,
                JobId = jobId,
                FilePath = filePath,
                RevisionKey = revisionKey,
                ModelId = modelId,
                LogicalModelName = logical,
                ProviderScope = providerScope,
                Source = source,
                ObservedAt = observedAt
            });
        await db.SaveChangesAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Code insight review-exposure capture failed; review execution continues.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
