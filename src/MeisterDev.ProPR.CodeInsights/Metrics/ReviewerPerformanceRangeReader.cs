// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Data;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>Reads retained joint cells under one consistent database snapshot, without source analysis.</summary>
public sealed class ReviewerPerformanceRangeReader(MeisterProPRDbContext dbContext, IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
{
    public const int MaximumDays = 366;
    public const int MaximumCells = 100_000;
    public const int MaximumSeries = 24;
    public const int MaximumPoints = 2_000;
    public const int MaximumRepositoryIdentityLength = ReviewerPerformanceIdentity.MaximumRepositoryIdentityLength;
    public const int MaximumModelIdentityLength = ReviewerPerformanceIdentity.MaximumModelIdentityLength;

    public async Task<ReviewerPerformanceRangeResponse> QueryAsync(
        ReviewerPerformanceQuery query, IReadOnlyCollection<Guid> authorizedClients, CancellationToken ct = default)
    {
        await using var created = contextFactory is not null ? await contextFactory.CreateDbContextAsync(ct) : null;
        var db = created ?? dbContext;
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var result = await this.QueryInContextAsync(db, query, authorizedClients, ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return result;
    }

    internal async Task<ReviewerPerformanceRangeResponse> QueryInContextAsync(
        MeisterProPRDbContext db, ReviewerPerformanceQuery query, IReadOnlyCollection<Guid> authorizedClients, CancellationToken ct)
    {
        Validate(query, authorizedClients);
        var captured = DateTimeOffset.UtcNow;
        var revision = db.Database.IsNpgsql()
            ? await db.Database.SqlQueryRaw<string>("SELECT txid_current_snapshot()::text AS \"Value\"").SingleAsync(ct)
            : null;
        var views = new List<ReviewerPerformanceViewResponse>();
        foreach (var (scope, viewIndex) in query.Views.Select((scope, index) => (scope, index)))
        {
            var clients = (scope.ClientIds ?? authorizedClients.ToArray()).ToArray();
            var snapshot = await ReviewerPerformanceCellReader.LoadAsync(db, scope, clients, ct);
            views.Add(ReviewerPerformanceViewBuilder.Build(query, scope, viewIndex, clients, snapshot));
        }

        return new(
            ReviewerPerformanceScoreCalculator.Version, captured, query,
            ReviewerPerformancePremises.All.Select(row => row.Id).ToArray(), views, revision);
    }


    public static string RepositoryKey(ReviewerPerformanceDailyCount row) => ReviewerPerformanceIdentity.RepositoryKey(row);
    public static string ModelKey(ReviewerPerformanceDailyCount row) => ReviewerPerformanceIdentity.ModelKey(row);

    internal static void Validate(ReviewerPerformanceQuery query, IReadOnlyCollection<Guid> clients) =>
        ReviewerPerformanceQueryValidator.Validate(query, clients);
}
