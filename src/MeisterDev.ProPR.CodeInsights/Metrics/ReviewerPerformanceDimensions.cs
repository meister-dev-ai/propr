// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceDimensions
{
    internal static bool MatchesEvidenceDimensions(ReviewerPerformanceDailyCount row, ReviewerPerformanceViewQuery scope)
    {
        if (ReviewerPerformanceMeasurement.IsCoverage(row))
        {
            return row.Outcome != "collectionModelOpportunity" || (scope.Models is null || scope.Models.Contains(ReviewerPerformanceIdentity.ModelKey(row)))
                && (scope.Types is null || row.TypeMembership.Length == 0 || MatchesTypes(row, scope.Types))
                && (scope.Qualifiers is null || row.Qualifier.Length == 0 || scope.Qualifiers.Contains(row.Qualifier));
        }

        return (scope.Models is null || scope.Models.Contains(ReviewerPerformanceIdentity.ModelKey(row)))
               && MatchesTypes(row, scope.Types)
               && (scope.Qualifiers is null || scope.Qualifiers.Contains(row.Qualifier))
               && (scope.Impacts is null || scope.Impacts.Contains(row.Impact.ToString()))
               && (scope.Likelihoods is null || scope.Likelihoods.Contains(row.Likelihood.ToString()))
               && (scope.RiskSeverities is null || scope.RiskSeverities.Contains(row.RiskSeverity.ToString()))
               && (scope.Confidences is null || scope.Confidences.Contains(row.Confidence.ToString()));
    }

    private static bool MatchesTypes(ReviewerPerformanceDailyCount row, IReadOnlyList<string>? types)
    {
        if (types is null)
        {
            return true;
        }

        var membership = row.TypeMembership.Length == 0
            ? new[] { "unclassified" }
            : row.TypeMembership.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return membership.Any(types.Contains);
    }

    internal static IEnumerable<ReviewerPerformanceFacet> Keys(ReviewerPerformanceDailyCount row, string dimension) => dimension switch
    {
        "client" => [new(row.ClientId.ToString(), row.ClientId.ToString())],
        "repository" =>
        [
            new(
                ReviewerPerformanceIdentity.RepositoryKey(row),
                $"{row.RepositoryId} · {(row.ProviderScope.Length == 0 ? "source scope unavailable" : row.ProviderScope)}")
        ],
        "model" =>
        [
            new(
                ReviewerPerformanceIdentity.ModelKey(row),
                row.ModelId.Length == 0
                    ? "Model attribution unavailable"
                    : $"{row.ModelId}{(row.LogicalModelName.Length == 0 ? "" : $" · {row.LogicalModelName}")}")
        ],
        "type" => (row.TypeMembership.Length == 0 ? new[] { "unclassified" } : row.TypeMembership.Split('|')).Select(type =>
            new ReviewerPerformanceFacet(type, type)),
        "qualifier" => [new(row.Qualifier, row.Qualifier.Length == 0 ? "Kind attribution unavailable" : row.Qualifier)],
        "impact" => [new(row.Impact.ToString(), RiskLabel(row.Impact.ToString()))],
        "likelihood" => [new(row.Likelihood.ToString(), RiskLabel(row.Likelihood.ToString()))],
        "riskSeverity" => [new(row.RiskSeverity.ToString(), RiskLabel(row.RiskSeverity.ToString()))],
        "confidence" => [new(row.Confidence.ToString(), RiskLabel(row.Confidence.ToString()))],
        _ => [],
    };

    private static string RiskLabel(string value) => value switch
    {
        "Unknown" => "Unassessed",
        "NotApplicable" => "Not applicable",
        _ => value,
    };

    internal static IReadOnlyList<ReviewerPerformanceFacet> FacetItems(List<ReviewerPerformanceDailyCount> cells, string dimension)
    {
        IEnumerable<ReviewerPerformanceDailyCount> representatives = dimension switch
        {
            "client" => cells.DistinctBy(row => row.ClientId),
            "repository" => cells.DistinctBy(row => (row.ClientId, row.ProviderScope, row.RepositoryId)),
            "model" => cells.DistinctBy(row => (row.ModelId, row.LogicalModelName)),
            "type" => cells.DistinctBy(row => row.TypeMembership),
            "qualifier" => cells.DistinctBy(row => row.Qualifier),
            "impact" => cells.DistinctBy(row => row.Impact),
            "likelihood" => cells.DistinctBy(row => row.Likelihood),
            "riskSeverity" => cells.DistinctBy(row => row.RiskSeverity),
            "confidence" => cells.DistinctBy(row => row.Confidence),
            _ => [],
        };
        return representatives.SelectMany(row => Keys(row, dimension)).DistinctBy(item => item.Id).OrderBy(item => item.Label).ToList();
    }

    internal static ReviewerPerformanceFacets Facets(List<ReviewerPerformanceDailyCount> cells)
    {
        var source = cells.Where(row => !ReviewerPerformanceMeasurement.IsCoverage(row)).ToList();
        return new(
            FacetItems(cells, "client"), FacetItems(cells, "repository"), FacetItems(
                cells.Where(row => !row.IsMiss
                                   && (!ReviewerPerformanceMeasurement.IsCoverage(row) || row.Outcome == "collectionModelOpportunity")).ToList(), "model"),
            FacetItems(source, "type"), FacetItems(source, "qualifier"),
            FacetItems(source.Where(row => !row.IsMiss).ToList(), "impact"),
            FacetItems(source.Where(row => !row.IsMiss).ToList(), "likelihood"),
            FacetItems(source.Where(row => !row.IsMiss).ToList(), "riskSeverity"),
            FacetItems(source.Where(row => !row.IsMiss).ToList(), "confidence"));
    }

    internal static List<ReviewerPerformanceDailyCount> Coalesce(List<ReviewerPerformanceDailyCount> cells)
    {
        var groups = cells.GroupBy(row => new
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
            row.IsMiss,
            row.IsClassified,
            row.PublicationState,
            row.DuplicateState
        });
        return groups.Select(group => CoalescedCell(group.Key.ClientId, group.Key.RepositoryId, group.First(), group.Sum(item => item.Count))).ToList();
    }

    private static ReviewerPerformanceDailyCount CoalescedCell(Guid clientId, string repositoryId, ReviewerPerformanceDailyCount representative, long count)
    {
        return new()
        {
            ClientId = clientId,
            RepositoryId = repositoryId,
            CodeInsightPullRequestId = representative.CodeInsightPullRequestId,
            PullRequestId = representative.PullRequestId,
            ProviderScope = representative.ProviderScope,
            BucketDate = representative.BucketDate,
            ModelId = representative.ModelId,
            LogicalModelName = representative.LogicalModelName,
            TypeMembership = representative.TypeMembership,
            Qualifier = representative.Qualifier,
            Impact = representative.Impact,
            Likelihood = representative.Likelihood,
            RiskSeverity = representative.RiskSeverity,
            Confidence = representative.Confidence,
            RiskRubricVersion = representative.RiskRubricVersion,
            Outcome = representative.Outcome,
            IsMiss = representative.IsMiss,
            IsClassified = representative.IsClassified,
            PublicationState = representative.PublicationState,
            DuplicateState = representative.DuplicateState,
            Count = count
        };
    }
}
