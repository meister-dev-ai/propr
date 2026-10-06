// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Metrics;

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
    IReadOnlyList<ReviewerPerformanceFacet> Qualifiers);

public sealed record ReviewerPerformancePoint(
    DateOnly Date,
    ReviewerPerformanceScore Score,
    IReadOnlyList<string> UnavailableReasons,
    DateOnly? WindowFrom = null,
    DateOnly? WindowTo = null);

public sealed record ReviewerPerformanceEvidenceMetadata(
    DateTimeOffset? OldestProjectionAt,
    DateTimeOffset? NewestProjectionAt,
    int ProjectionVersion,
    int PendingSourceAggregates);

public sealed record ReviewerPerformanceSeries(string Id, string Label, IReadOnlyList<ReviewerPerformancePoint> Points);

public sealed record ReviewerPerformanceMatrixCell(string RowId, string RowLabel, string ColumnId, string ColumnLabel, ReviewerPerformancePoint Measurement);

public sealed record ReviewerPerformanceViewResponse(
    ReviewerPerformanceViewQuery Scope,
    ReviewerPerformanceFacets Facets,
    IReadOnlyList<ReviewerPerformanceSeries> Series,
    IReadOnlyList<ReviewerPerformanceMatrixCell> Breakdown,
    long AggregateCells,
    ReviewerPerformanceEvidenceMetadata Evidence);

/// <summary>Complete versioned response suitable for immutable capture.</summary>
public sealed record ReviewerPerformanceRangeResponse(
    string CalculationVersion,
    DateTimeOffset CapturedAt,
    ReviewerPerformanceQuery Query,
    IReadOnlyList<string> PremiseIds,
    IReadOnlyList<ReviewerPerformanceViewResponse> Views,
    string? EvidenceRevision = null);

public sealed record ReviewerPerformanceSaveReportRequest(Guid Id, string Name, ReviewerPerformanceQuery Query);

public sealed record ReviewerPerformanceReportSummary(Guid Id, string Name, string CalculationVersion, DateTimeOffset CapturedAt, DateTimeOffset ExpiresAt);

public sealed record ReviewerPerformanceSavedReport(ReviewerPerformanceReportSummary Report, ReviewerPerformanceRangeResponse Response, bool CompatibleVersion);
