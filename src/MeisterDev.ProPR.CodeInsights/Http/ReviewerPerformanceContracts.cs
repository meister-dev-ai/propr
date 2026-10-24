// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Metrics;
using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.CodeInsights.Http;

/// <summary>A bounded query for one or two views of retained evidence.</summary>
public sealed record ReviewerPerformanceQuery
{
    public string Bucket { get; init; } = "day";
    public string Aggregation { get; init; } = "cumulative";
    public string Grouping { get; init; } = "none";
    public IReadOnlyList<ReviewerPerformanceViewQuery> Views { get; init; } = [];
    public ReviewerPerformanceBreakdownQuery? Breakdown { get; init; }
}

/// <summary>Null selections include the authorized population; empty selections include nothing.</summary>
public sealed record ReviewerPerformanceViewQuery
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public IReadOnlyList<Guid>? ClientIds { get; init; }
    public IReadOnlyList<string>? Repositories { get; init; }
    public IReadOnlyList<string>? Models { get; init; }
    public IReadOnlyList<string>? Types { get; init; }
    public IReadOnlyList<string>? Qualifiers { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Impacts { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Likelihoods { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RiskSeverities { get; init; }

    /// <summary>Correctness confidence selections, including Unknown for unassessed findings.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Confidences { get; init; }

    /// <summary>Risk selections require independently assessed human concerns for recall.</summary>
    [JsonIgnore]
    public bool HasRiskSelection => this.Impacts is not null || this.Likelihoods is not null || this.RiskSeverities is not null || this.Confidences is not null;
}

/// <summary>Two dimensions for a bounded supplementary matrix.</summary>
public sealed record ReviewerPerformanceBreakdownQuery(string Rows, string Columns, DateOnly? Date = null, int ViewIndex = 0);

/// <summary>Stable metadata identifier and display label.</summary>
public sealed record ReviewerPerformanceFacet(string Id, string Label);

public sealed record ReviewerPerformanceFacets(
    IReadOnlyList<ReviewerPerformanceFacet> Clients,
    IReadOnlyList<ReviewerPerformanceFacet> Repositories,
    IReadOnlyList<ReviewerPerformanceFacet> Models,
    IReadOnlyList<ReviewerPerformanceFacet> Types,
    IReadOnlyList<ReviewerPerformanceFacet> Qualifiers,
    IReadOnlyList<ReviewerPerformanceFacet>? Impacts = null,
    IReadOnlyList<ReviewerPerformanceFacet>? Likelihoods = null,
    IReadOnlyList<ReviewerPerformanceFacet>? RiskSeverities = null,
    IReadOnlyList<ReviewerPerformanceFacet>? Confidences = null);

/// <summary>A covered-population score with independently supported overall precision.</summary>
public sealed record ReviewerPerformancePoint(
    DateOnly Date,
    ReviewerPerformanceScore Score,
    IReadOnlyList<string> UnavailableReasons,
    DateOnly? WindowFrom = null,
    DateOnly? WindowTo = null,
    ReviewerPerformancePrecisionScore? OverallPrecision = null,
    ReviewerPerformanceCoverage? Coverage = null);

/// <summary>Advisory coverage and sample minimums captured with the measurement response.</summary>
public sealed record ReviewerPerformanceCoveragePolicy(int MinimumCoveragePercentage, int MinimumCoveredPullRequests);

/// <summary>Distinct excluded source pull requests carrying one evidence limitation.</summary>
public sealed record ReviewerPerformanceExclusionReasonCount(string Reason, int PullRequests);

/// <summary>Source pull-request coverage before contribution filters remove incompatible misses.</summary>
public sealed record ReviewerPerformanceCoverage(
    int CandidatePullRequests,
    int CoveredPullRequests,
    int ExcludedPullRequests,
    double? Percentage,
    bool MeetsMinimum,
    bool RecallSupported,
    IReadOnlyList<ReviewerPerformanceExclusionReasonCount> Reasons,
    IReadOnlyList<int> ExclusionIds);

/// <summary>Captured exclusion identity and bounded recovery metadata, including queued, active and completed collection; source URLs may be unavailable.</summary>
public sealed record ReviewerPerformanceExclusion(
    int Id,
    Guid AggregateId,
    Guid ClientId,
    string ClientName,
    string RepositoryId,
    string? RepositoryName,
    long PullRequestId,
    string ProviderScope,
    IReadOnlyList<string> Reasons,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NotBefore,
    DateTimeOffset? LastCompletedAt,
    string? LastRefreshReason,
    string RecoveryDisposition,
    bool? CollectionEnabled,
    string? SourceUrl = null,
    string? RecoveryAction = null,
    DateTimeOffset? LastModelRetryAt = null,
    DateTimeOffset? ModelRetryNotBefore = null,
    Guid? SourceConnectionId = null);

public sealed record ReviewerPerformanceEvidenceMetadata(
    DateTimeOffset? OldestProjectionAt,
    DateTimeOffset? NewestProjectionAt,
    int ProjectionVersion,
    int PendingSourceAggregates,
    string? ModelEvidenceSchema = null,
    IReadOnlyList<string>? RiskRubricVersions = null);

public sealed record ReviewerPerformanceSeries(string Id, string Label, IReadOnlyList<ReviewerPerformancePoint> Points);

public sealed record ReviewerPerformanceMatrixCell(string RowId, string RowLabel, string ColumnId, string ColumnLabel, ReviewerPerformancePoint Measurement);

public sealed record ReviewerPerformanceViewResponse(
    ReviewerPerformanceViewQuery Scope,
    ReviewerPerformanceFacets Facets,
    IReadOnlyList<ReviewerPerformanceSeries> Series,
    IReadOnlyList<ReviewerPerformanceMatrixCell> Breakdown,
    long AggregateCells,
    ReviewerPerformanceEvidenceMetadata Evidence,
    IReadOnlyList<ReviewerPerformanceExclusion>? ExclusionCatalog = null);

/// <summary>Complete versioned response suitable for immutable capture.</summary>
public sealed record ReviewerPerformanceRangeResponse(
    string CalculationVersion,
    DateTimeOffset CapturedAt,
    ReviewerPerformanceQuery Query,
    IReadOnlyList<string> PremiseIds,
    IReadOnlyList<ReviewerPerformanceViewResponse> Views,
    string? EvidenceRevision = null,
    ReviewerPerformanceCoveragePolicy? CoveragePolicy = null);

public sealed record ReviewerPerformanceSaveReportRequest(Guid Id, string Name, ReviewerPerformanceQuery Query);

/// <summary>Captured source identity for a bounded retained-classification retry.</summary>
public sealed record ReviewerPerformanceRetryModelRequest(string ProviderScope, Guid ConnectionId);

public sealed record ReviewerPerformanceReportSummary(Guid Id, string Name, string CalculationVersion, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt);

public sealed record ReviewerPerformanceSavedReport(ReviewerPerformanceReportSummary Report, ReviewerPerformanceRangeResponse Response, bool CompatibleVersion);
