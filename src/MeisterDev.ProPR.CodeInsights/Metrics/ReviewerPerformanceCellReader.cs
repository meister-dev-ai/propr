// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal sealed record ReviewerPerformanceViewSnapshot(
    List<ReviewerPerformanceDailyCount> Cells,
    DateTimeOffset? NewestProjection,
    int Pending,
    IReadOnlyDictionary<Guid, string> ClientNames,
    IReadOnlyDictionary<(Guid ClientId, string RepositoryId), string?> RepositoryNames,
    IReadOnlyDictionary<Guid, CodeInsightPullRequest> Aggregates,
    IReadOnlyDictionary<Guid, bool> CollectionEnabled,
    IReadOnlyDictionary<ReviewerPerformanceSourceKey, string?>? SourceUrls = null,
    ReviewerPerformanceModelEvidence? ModelEvidence = null,
    TimeSpan? ModelRetryCooldown = null);

internal static class ReviewerPerformanceCellReader
{
    internal static async Task<ReviewerPerformanceViewSnapshot> LoadAsync(
        MeisterProPRDbContext db, ReviewerPerformanceViewQuery scope, Guid[] clients, CancellationToken ct, bool includeModelEvidence = false,
        DateTimeOffset? capturedAt = null, TimeSpan? modelRetryCooldown = null)
    {
        var cellsQuery = db.ReviewerPerformanceDailyCounts.AsNoTracking()
            .Where(row => clients.Contains(row.ClientId) && row.BucketDate >= scope.From && row.BucketDate <= scope.To);
        var newestProjection = await cellsQuery.Select(row => (DateTimeOffset?)row.UpdatedAt).MaxAsync(ct);
        var cells = await LoadBoundedCellsAsync(cellsQuery, ct);
        var clientNames = await LoadClientNamesAsync(db, clients, ct);
        var repositoryNames = await LoadRepositoryNamesAsync(db, clients, cellsQuery, ct);
        var aggregates = await db.CodeInsightPullRequests.AsNoTracking()
            .Where(pr => clients.Contains(pr.ClientId) && cellsQuery.Any(cell => cell.CodeInsightPullRequestId == pr.Id
                                                                                 && cell.ClientId == pr.ClientId && cell.RepositoryId == pr.RepositoryId))
            .ToDictionaryAsync(pr => pr.Id, ct);
        var collectionEnabled = await db.Clients.AsNoTracking().Where(client => clients.Contains(client.Id))
            .ToDictionaryAsync(client => client.Id, client => client.CodeInsightsCollectionEnabled, ct);
        var pending = await ReviewerPerformanceProjectionQueries.Stale(db).AsNoTracking()
            .Where(pr => clients.Contains(pr.ClientId)).CountAsync(ct);
        var modelEvidence = includeModelEvidence
            ? await ReviewerPerformanceModelEvidenceReader.LoadAsync(db, aggregates, cells, capturedAt ?? DateTimeOffset.UtcNow, ct)
            : null;
        if (modelEvidence is not null)
        {
            cells.AddRange(modelEvidence.Markers(scope.From, scope.To));
            if (cells.Count > ReviewerPerformanceRangeReader.MaximumCells)
            {
                throw new ArgumentException("The scope exceeds the retained-cell limit. Narrow the dates or clients.");
            }
        }

        return new(
            cells, newestProjection, pending, clientNames, repositoryNames, aggregates, collectionEnabled, ModelEvidence: modelEvidence,
            ModelRetryCooldown: modelRetryCooldown);
    }

    private static async Task<List<ReviewerPerformanceDailyCount>> LoadBoundedCellsAsync(
        IQueryable<ReviewerPerformanceDailyCount> cellsQuery, CancellationToken ct)
    {
        var cells = await cellsQuery
            .GroupBy(row => new
            {
                row.CodeInsightPullRequestId,
                row.PullRequestId,
                row.ClientId,
                row.RepositoryId,
                row.ProviderScope,
                row.BucketDate,
                row.ModelId,
                row.LogicalModelName,
                row.TypeMembership,
                row.Qualifier,
                row.Impact, row.Likelihood, row.RiskSeverity, row.Confidence, row.RiskRubricVersion,
                row.Outcome,
                row.PublicationState,
                row.DuplicateState,
                row.IsMiss,
                row.IsClassified
            })
            .Select(group => new ReviewerPerformanceDailyCount
            {
                CodeInsightPullRequestId = group.Key.CodeInsightPullRequestId,
                PullRequestId = group.Key.PullRequestId,
                ClientId = group.Key.ClientId,
                RepositoryId = group.Key.RepositoryId,
                ProviderScope = group.Key.ProviderScope,
                BucketDate = group.Key.BucketDate,
                ModelId = group.Key.ModelId,
                LogicalModelName = group.Key.LogicalModelName,
                TypeMembership = group.Key.TypeMembership,
                Qualifier = group.Key.Qualifier,
                Impact = group.Key.Impact, Likelihood = group.Key.Likelihood, RiskSeverity = group.Key.RiskSeverity, Confidence = group.Key.Confidence,
                RiskRubricVersion = group.Key.RiskRubricVersion,
                Outcome = group.Key.Outcome,
                PublicationState = group.Key.PublicationState,
                DuplicateState = group.Key.DuplicateState,
                IsMiss = group.Key.IsMiss,
                IsClassified = group.Key.IsClassified,
                Count = group.Sum(row => row.Count),
                UpdatedAt = group.Min(row => row.UpdatedAt)
            })
            .Take(ReviewerPerformanceRangeReader.MaximumCells + 1)
            .ToListAsync(ct);
        if (cells.Count > ReviewerPerformanceRangeReader.MaximumCells)
        {
            throw new ArgumentException("The scope exceeds the retained-cell limit. Narrow the dates or clients.");
        }

        // Repository selection and facets use the same unknown source identity as coverage assessment.
        foreach (var row in cells)
        {
            row.ProviderScope = ReviewerPerformanceSourceKey.Normalize(row.ProviderScope);
        }

        return cells;
    }

    private static async Task<IReadOnlyDictionary<Guid, string>> LoadClientNamesAsync(MeisterProPRDbContext db, Guid[] clients, CancellationToken ct)
    {
        return await db.Clients.AsNoTracking()
            .Where(row => clients.Contains(row.Id))
            .Select(row => new
            {
                row.Id,
                row.DisplayName
            })
            .ToDictionaryAsync(row => row.Id, row => row.DisplayName, ct);
    }

    private static async Task<IReadOnlyDictionary<(Guid ClientId, string RepositoryId), string?>> LoadRepositoryNamesAsync(
        MeisterProPRDbContext db, Guid[] clients, IQueryable<ReviewerPerformanceDailyCount> cellsQuery, CancellationToken ct)
    {
        return await db.CodeInsightPullRequests.AsNoTracking()
            .Where(row => clients.Contains(row.ClientId) && cellsQuery.Any(cell => cell.ClientId == row.ClientId && cell.RepositoryId == row.RepositoryId))
            .GroupBy(row => new
            {
                row.ClientId,
                row.RepositoryId
            })
            .Select(group => new
            {
                group.Key.ClientId,
                group.Key.RepositoryId,
                RepositoryName = db.CodeInsightPullRequests
                    .Where(row => row.ClientId == group.Key.ClientId && row.RepositoryId == group.Key.RepositoryId)
                    .OrderByDescending(row => row.UpdatedAt)
                    .Select(row => row.RepositoryName)
                    .FirstOrDefault()
            })
            .ToDictionaryAsync(row => (row.ClientId, row.RepositoryId), row => row.RepositoryName, ct);
    }
}
