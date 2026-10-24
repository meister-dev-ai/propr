// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal readonly record struct ReviewerPerformanceSourceKey(Guid AggregateId, string ProviderScope)
{
    internal static ReviewerPerformanceSourceKey Of(ReviewerPerformanceDailyCount row) => new(row.CodeInsightPullRequestId, Normalize(row.ProviderScope));

    internal static string Normalize(string scope) => string.IsNullOrWhiteSpace(scope) ? string.Empty : scope;
}

internal sealed record ReviewerPerformanceCoverageResult(
    ReviewerPerformanceCoverage Coverage,
    HashSet<ReviewerPerformanceSourceKey> CoveredSources,
    IReadOnlyList<string> Reasons);

/// <summary>Classifies source pull requests before contribution filters remove unsupported evidence.</summary>
internal sealed class ReviewerPerformanceCoverageContext(
    ReviewerPerformanceCoveragePolicy policy,
    ReviewerPerformanceViewSnapshot? snapshot = null,
    DateTimeOffset? capturedAt = null)
{
    private const int MaximumExclusionReferences = ReviewerPerformanceRangeReader.MaximumCells * 10;
    private readonly Dictionary<(ReviewerPerformanceSourceKey Source, string Reasons), int> _catalogKeys = [];
    private readonly List<ReviewerPerformanceExclusion> _catalog = [];
    private int _references;

    internal IReadOnlyList<ReviewerPerformanceExclusion> Catalog => this._catalog;
    internal bool HasModelEvidence => snapshot?.ModelEvidence is not null;

    internal ReviewerPerformanceCounts ModelMisses(
        ReviewerPerformanceViewQuery scope, HashSet<ReviewerPerformanceSourceKey> covered, DateOnly from, DateOnly to) =>
        snapshot?.ModelEvidence?.Misses(scope, covered, from, to) ?? default;

    internal static List<ReviewerPerformanceDailyCount> CompactPopulation(List<ReviewerPerformanceDailyCount> population, bool cumulative)
    {
        // Coverage needs source membership and evidence availability, independent of publication dimensions.
        return population.GroupBy(row => new
        {
            row.CodeInsightPullRequestId,
            row.ClientId,
            row.RepositoryId,
            row.ProviderScope,
            row.PullRequestId,
            BucketDate = cumulative ? default : row.BucketDate,
            Outcome = row.IsMiss ? row.Outcome : ReviewerPerformanceMeasurement.IsCoverage(row) ? row.Outcome : "finding",
            row.IsMiss,
            row.IsClassified,
            TypeKnown = row.TypeMembership.Length != 0,
            QualifierKnown = row.Qualifier.Length != 0,
        }).Select(group => new ReviewerPerformanceDailyCount
        {
            CodeInsightPullRequestId = group.Key.CodeInsightPullRequestId,
            ClientId = group.Key.ClientId,
            RepositoryId = group.Key.RepositoryId,
            ProviderScope = group.Key.ProviderScope,
            PullRequestId = group.Key.PullRequestId,
            BucketDate = cumulative ? group.Min(row => row.BucketDate) : group.Key.BucketDate,
            Outcome = group.Key.Outcome,
            IsMiss = group.Key.IsMiss,
            IsClassified = group.Key.IsClassified,
            TypeMembership = group.Key.TypeKnown ? "known" : string.Empty,
            Qualifier = group.Key.QualifierKnown ? "known" : string.Empty,
        }).ToList();
    }

    internal ReviewerPerformanceCoverageResult Assess(
        IReadOnlyList<ReviewerPerformanceDailyCount> population, ReviewerPerformanceViewQuery scope, string grouping, DateOnly? to = null)
    {
        var covered = new HashSet<ReviewerPerformanceSourceKey>();
        var excluded = new List<int>();
        var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = 0;
        foreach (var aggregate in population.GroupBy(row => row.CodeInsightPullRequestId))
        {
            var scopes = aggregate.GroupBy(row => ReviewerPerformanceSourceKey.Normalize(row.ProviderScope), StringComparer.Ordinal).ToArray();
            var unknown = scopes.FirstOrDefault(source => string.IsNullOrWhiteSpace(source.Key));
            var known = scopes.Where(source => !string.IsNullOrWhiteSpace(source.Key)).ToArray();
            foreach (var source in known.Length == 0 ? scopes : known)
            {
                // Unattributed evidence cannot establish which known source on this aggregate owns it.
                var rows = unknown is not null && !ReferenceEquals(source, unknown) ? source.Concat(unknown).ToArray() : source.ToArray();
                var key = new ReviewerPerformanceSourceKey(aggregate.Key, source.Key);
                var model = scope.Models is not null ? snapshot?.ModelEvidence?.Assess(key, scope, to ?? scope.To) : null;
                if (model is { Member: false })
                {
                    continue;
                }

                candidates++;
                var reasons = model is not null ? model.Reasons.ToArray() : ExclusionReasons(rows, scope, grouping);
                if (model is not null && rows.Any(row =>
                        string.IsNullOrWhiteSpace(row.ProviderScope) && (!row.IsMiss || row.Outcome is not ("missExcluded" or "missProvisional"))))
                {
                    reasons = reasons.Append("model-concern-source-unavailable").Distinct(StringComparer.Ordinal).ToArray();
                }

                if (reasons.Length == 0)
                {
                    covered.Add(key);
                    continue;
                }

                excluded.Add(this.AddExclusion(key, source.First(), reasons, scope));
                foreach (var reason in reasons)
                {
                    reasonCounts[reason] = reasonCounts.GetValueOrDefault(reason) + 1;
                }
            }
        }

        this._references = checked(this._references + excluded.Count);
        if (this._references > MaximumExclusionReferences)
        {
            throw new ArgumentException("The selection exceeds the captured-exclusion limit. Narrow the dates or series.");
        }

        var supported = scope.Models is null && grouping != "model" || scope.Models is not null && snapshot?.ModelEvidence is not null;
        var riskRestricted = scope.HasRiskSelection || grouping is "impact" or "likelihood" or "riskSeverity" or "confidence";
        supported = supported && !riskRestricted;
        var meetsMinimum = covered.Count >= policy.MinimumCoveredPullRequests
                           && (long)covered.Count * 100 >= (long)candidates * policy.MinimumCoveragePercentage;
        var limits = reasonCounts.Keys.Order(StringComparer.Ordinal).ToList();
        if (!supported && !riskRestricted)
        {
            limits.Add("miss-model-attribution-unavailable");
        }

        if (riskRestricted)
        {
            limits.Add("miss-risk-assessment-unavailable");
        }

        if (covered.Count < policy.MinimumCoveredPullRequests)
        {
            limits.Add("insufficient-covered-pull-requests");
        }

        if (candidates > 0 && (long)covered.Count * 100 < (long)candidates * policy.MinimumCoveragePercentage)
        {
            limits.Add("coverage-below-minimum");
        }

        return new(
            new(
                candidates, covered.Count, excluded.Count, candidates == 0 ? null : 100d * covered.Count / candidates,
                meetsMinimum, supported,
                reasonCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ReviewerPerformanceExclusionReasonCount(pair.Key, pair.Value))
                    .ToArray(),
                excluded), covered, limits);
    }

    private static string[] ExclusionReasons(ReviewerPerformanceDailyCount[] rows, ReviewerPerformanceViewQuery scope, string grouping)
    {
        var reasons = new List<string>();
        if (!rows.Any(row => row.Outcome is "collectionComplete" or "collectionProvisional") || rows.Any(row => row.Outcome == "collectionUnknown"))
        {
            reasons.Add("miss-collection-coverage-unavailable");
        }

        var misses = rows.Where(row => row.IsMiss && row.Outcome != "missExcluded").ToArray();
        if (misses.Any(row => row.Outcome == "missFailed"))
        {
            reasons.Add("miss-judgement-failed");
        }

        var settled = misses.Where(row => row.Outcome != "missProvisional").ToArray();
        if ((scope.Types is not null || grouping == "type") && settled.Any(row => row.TypeMembership.Length == 0 || !row.IsClassified))
        {
            reasons.Add("miss-type-attribution-unavailable");
        }

        if ((scope.Qualifiers is not null || grouping == "qualifier") && settled.Any(row => row.Qualifier.Length == 0 || !row.IsClassified))
        {
            reasons.Add("miss-qualifier-attribution-unavailable");
        }

        if (rows.Any(row => string.IsNullOrWhiteSpace(row.ProviderScope) && (!row.IsMiss || row.Outcome is not ("missExcluded" or "missProvisional"))))
        {
            reasons.Add("miss-provider-scope-unavailable");
        }

        return reasons.ToArray();
    }

    private int AddExclusion(ReviewerPerformanceSourceKey key, ReviewerPerformanceDailyCount source, string[] reasons, ReviewerPerformanceViewQuery scope)
    {
        var action = scope.Models is null ? null : snapshot?.ModelEvidence?.RecoveryAction(key, scope, reasons);
        var catalogKey = (key, string.Join('|', reasons) + ":" + action);
        if (this._catalogKeys.TryGetValue(catalogKey, out var existing))
        {
            return existing;
        }

        if (this._catalog.Count >= ReviewerPerformanceRangeReader.MaximumCells)
        {
            throw new ArgumentException("The selection exceeds the exclusion-catalog limit. Narrow the dates or series.");
        }

        var aggregate = snapshot?.Aggregates.GetValueOrDefault(key.AggregateId);
        var clientName = snapshot?.ClientNames.GetValueOrDefault(source.ClientId) ?? source.ClientId.ToString();
        var repositoryName = snapshot?.RepositoryNames.GetValueOrDefault((source.ClientId, source.RepositoryId));
        var refreshReason = CodeInsightHistoricalRecoveryPolicy.SafeReason(aggregate?.LastHistoricalRefreshReason);
        var enabled = snapshot?.CollectionEnabled.TryGetValue(source.ClientId, out var collected) == true ? collected : (bool?)null;
        var modelSource = snapshot?.ModelEvidence?.RecoverySource(key);
        var retryAt = action == "retry-model-evidence" ? modelSource?.LastRetryAt : null;
        var retryNotBefore = retryAt + (snapshot?.ModelRetryCooldown ?? TimeSpan.FromMinutes(5));
        var disposition = action switch
        {
            "none" => "unreconstructable-history",
            "retry-model-evidence" => enabled == false ? "collection-disabled" : retryNotBefore > (capturedAt ?? DateTimeOffset.UtcNow) ? "cooldown" : "ready",
            _ => CodeInsightHistoricalRecoveryPolicy.Disposition(aggregate, enabled, capturedAt ?? DateTimeOffset.UtcNow),
        };
        var id = this._catalog.Count;
        this._catalog.Add(
            new(
                id, key.AggregateId, source.ClientId, clientName, source.RepositoryId, repositoryName,
                source.PullRequestId, key.ProviderScope, reasons, aggregate?.LastHistoricalRefreshAttemptAt,
                aggregate?.HistoricalRefreshNotBefore, aggregate?.LastHistoricalRefreshCompletedAt, refreshReason,
                disposition, enabled, snapshot?.SourceUrls?.GetValueOrDefault(key), action, retryAt, retryNotBefore, modelSource?.ConnectionId));
        this._catalogKeys.Add(catalogKey, id);
        return id;
    }
}
