// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

internal static class ReviewerPerformanceProjectionBuilder
{
    internal static List<ReviewerPerformanceDailyCount> Build(
        CodeInsightPullRequest pr, ReviewerPerformanceProjectionEvidence evidence, DateTimeOffset projectionCutoff)
    {
        var cells = new List<ReviewerPerformanceDailyCount>();
        AddFindings(cells, evidence);
        AddMisses(cells, evidence.Misses);
        AddCoverage(cells, pr, evidence);
        var projected = new List<ReviewerPerformanceDailyCount>();
        foreach (var group in cells.GroupBy(row => new
                 {
                     row.BucketDate,
                     row.ProviderScope,
                     row.ModelId,
                     row.LogicalModelName,
                     row.TypeMembership,
                     row.Qualifier,
                     row.Outcome,
                     row.PublicationState,
                     row.DuplicateState,
                     row.IsMiss,
                     row.IsClassified
                 }))
        {
            var row = group.First();
            row.Id = Guid.CreateVersion7();
            row.CodeInsightPullRequestId = pr.Id;
            row.ClientId = pr.ClientId;
            row.RepositoryId = pr.RepositoryId;
            row.PullRequestId = pr.PullRequestId;
            row.CellKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(group.Key))));
            row.Count = group.LongCount();
            row.UpdatedAt = projectionCutoff;
            projected.Add(row);
        }

        return projected;
    }

    private static void AddFindings(List<ReviewerPerformanceDailyCount> cells, ReviewerPerformanceProjectionEvidence evidence)
    {
        var findings = evidence.Findings;
        var dispositions = evidence.Dispositions;
        var memberships = evidence.Memberships;
        var publications = new HashSet<(string Scope, string ThreadId, string CommentId)>();
        foreach (var finding in findings.OrderBy(row => row.ObservedAt).ThenBy(row => row.Id))
        {
            dispositions.TryGetValue(finding.Id, out var initial);
            var publicationState = ResolvePublicationState(finding, publications);

            cells.Add(
                new()
                {
                    BucketDate = DateOnly.FromDateTime(finding.ObservedAt.UtcDateTime),
                    ProviderScope = finding.ProviderScope,
                    ModelId = finding.OriginModelId ?? string.Empty,
                    LogicalModelName = finding.OriginLogicalModelName ?? string.Empty,
                    TypeMembership = memberships.GetValueOrDefault(finding.Id, string.Empty),
                    Qualifier = finding.Qualifier?.ToString() ?? string.Empty,
                    IsClassified = finding.ClassifiedAt is not null || memberships.ContainsKey(finding.Id),
                    Outcome = Outcome(
                        finding.NativeStatus ?? initial?.NativeStatus, finding.CurrentDisposition ?? initial?.Disposition,
                        finding.CurrentDisposition is not null ? finding.CurrentClassifierVersion : initial?.ClassifierVersion,
                        finding.CurrentDisposition is not null ? finding.CurrentClassifierConfidence : initial?.ClassifierConfidence),
                    PublicationState = publicationState,
                    DuplicateState = finding.DuplicateState,
                    Count = 1,
                });
        }
    }

    private static void AddMisses(List<ReviewerPerformanceDailyCount> cells, IReadOnlyList<ReviewerPerformanceMissEvidence> misses)
    {
        foreach (var miss in misses)
        {
            cells.Add(
                new()
                {
                    BucketDate = DateOnly.FromDateTime(miss.HarvestedAt.UtcDateTime),
                    IsMiss = true,
                    ProviderScope = miss.ProviderScope,
                    TypeMembership = miss.TypeMembership,
                    Qualifier = miss.Qualifier?.ToString() ?? string.Empty,
                    IsClassified = miss.DimensionClassifierVersion is not null && !miss.DimensionJudgementFailed,
                    Outcome = MissOutcome(miss),
                    Count = 1,
                });
        }
    }

    private static void AddCoverage(List<ReviewerPerformanceDailyCount> cells, CodeInsightPullRequest pr, ReviewerPerformanceProjectionEvidence evidence)
    {
        var exposures = evidence.Exposures;
        var coverage = evidence.Coverage;
        var cohorts = cells.Select(row => (row.BucketDate, row.ProviderScope)).ToHashSet();
        foreach (var exposure in exposures)
        {
            cohorts.Add((DateOnly.FromDateTime(exposure.ObservedAt.UtcDateTime), exposure.ProviderScope));
        }

        if (coverage.Count == 0 && pr.MissHarvestObservedAt is not null)
        {
            coverage.Add(
                new()
                {
                    CodeInsightPullRequestId = pr.Id,
                    ProviderScope = pr.MissHarvestProviderScope,
                    ObservedAt = pr.MissHarvestObservedAt.Value,
                    EnumerationComplete = true,
                    AllHumanThreadsResolved = pr.MissHarvestSettled
                });
        }

        foreach (var source in coverage.Where(source => !cohorts.Any(cohort => cohort.ProviderScope == source.ProviderScope)))
        {
            cohorts.Add((DateOnly.FromDateTime(source.ObservedAt.UtcDateTime), source.ProviderScope));
        }

        foreach (var (date, providerScope) in cohorts)
        {
            var source = coverage.FirstOrDefault(row => row.ProviderScope == providerScope);
            cells.Add(
                new()
                {
                    BucketDate = date,
                    ProviderScope = providerScope,
                    Outcome = CoverageOutcome(source),
                    Count = 1
                });
        }
    }

    private static CodeInsightPublicationState ResolvePublicationState(
        ReviewerPerformanceFindingEvidence finding, HashSet<(string Scope, string ThreadId, string CommentId)> publications)
    {
        var state = finding.PublicationState;
        var publicationId = finding.ProviderCommentId ?? finding.ProviderThreadId;
        if (state == CodeInsightPublicationState.Unknown && !string.IsNullOrEmpty(publicationId))
        {
            state = CodeInsightPublicationState.Published;
        }

        if (state == CodeInsightPublicationState.Published && string.IsNullOrEmpty(publicationId))
        {
            state = CodeInsightPublicationState.Unknown;
        }

        if (state == CodeInsightPublicationState.Published &&
            !publications.Add((finding.ProviderScope, finding.ProviderThreadId ?? "", finding.ProviderCommentId ?? "")))
        {
            return CodeInsightPublicationState.SuppressedRepeat;
        }

        return state;
    }

    private static string MissOutcome(ReviewerPerformanceMissEvidence miss)
    {
        if (miss.ExcludedAsOwnFinding)
        {
            return "missExcluded";
        }

        if (miss.JudgementFailed)
        {
            return "missFailed";
        }

        if (!miss.JudgedThreadResolved)
        {
            return "missProvisional";
        }

        if (!miss.IsSubstantive || !miss.IsInScope)
        {
            return "missExcluded";
        }

        return miss.WasActedOn ? "missActed" : "missUnacted";
    }

    private static string CoverageOutcome(CodeInsightHarvestCoverage? source)
    {
        if (source is null || !source.EnumerationComplete)
        {
            return "collectionUnknown";
        }

        return source.AllHumanThreadsResolved ? "collectionComplete" : "collectionProvisional";
    }

    private static string Outcome(string? native, CodeInsightDisposition? disposition, string? version, double? confidence)
    {
        if (IsNativeStatus(native, "Active", "Pending"))
        {
            return "unresolved";
        }

        if (!IsNativeStatus(native, "Fixed", "Closed", "WontFix", "ByDesign") || disposition is null)
        {
            return "unknown";
        }

        if (version is not null && confidence is null || string.IsNullOrWhiteSpace(native))
        {
            return "unknown";
        }

        if (string.Equals(native, "WontFix", StringComparison.OrdinalIgnoreCase))
        {
            return "wontFix";
        }

        if (string.Equals(native, "ByDesign", StringComparison.OrdinalIgnoreCase))
        {
            return "byDesign";
        }

        return disposition switch
        {
            CodeInsightDisposition.Addressed or CodeInsightDisposition.Acknowledged => "positive",
            CodeInsightDisposition.FalsePositive => "wrong",
            CodeInsightDisposition.Dismissed => "dismissed",
            CodeInsightDisposition.Discussed => "unresolved",
            _ => "unknown"
        };
    }

    private static bool IsNativeStatus(string? value, params string[] statuses)
    {
        return statuses.Contains(value, StringComparer.OrdinalIgnoreCase);
    }
}
