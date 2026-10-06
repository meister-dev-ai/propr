// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceViewBuilder
{
    private const int MaximumMatrixAxisValues = 12;
    private const int MaximumMatrixCells = MaximumMatrixAxisValues * MaximumMatrixAxisValues;

    internal static ReviewerPerformanceViewResponse Build(
        ReviewerPerformanceQuery query,
        ReviewerPerformanceViewQuery scope,
        int viewIndex,
        Guid[] clients,
        ReviewerPerformanceViewSnapshot snapshot)
    {
        var raw = snapshot.Cells;
        var repositoryMatcher = new ReviewerPerformanceRepositoryMatcher();
        // Unknown source attribution remains in the population used to assess repository coverage.
        var viewPopulation = SelectViewPopulation(raw, scope, repositoryMatcher.Prepare(scope.Repositories));
        var population = viewPopulation.Broader;
        var selected = viewPopulation.Selected;
        var labels = new ReviewerPerformanceLabelResolver(snapshot.ClientNames, snapshot.RepositoryNames, raw);
        var facets = labels.Apply(ReviewerPerformanceDimensions.Facets(raw));
        var preserveScope = RequiresRepositoryOrClientScope(query);
        var measurementPopulation = ReviewerPerformanceDimensions.Coalesce(population, preserveScope);
        var measurementSelected = ReviewerPerformanceDimensions.Coalesce(selected, preserveScope);
        var dates = ReviewerPerformanceBuckets.Dates(scope.From, scope.To, query.Bucket).ToList();
        var series = BuildSeries(query, scope, selected, measurementSelected, measurementPopulation, dates, labels, repositoryMatcher);
        var matrix = BuildMatrix(query, scope, viewIndex, measurementSelected, measurementPopulation, dates, labels, repositoryMatcher);
        var freshness = new ReviewerPerformanceEvidenceMetadata(
            raw.Count == 0 ? null : raw.Min(row => row.UpdatedAt),
            snapshot.NewestProjection,
            Rollups.ReviewerPerformanceCountProjector.ProjectionVersion,
            snapshot.Pending);
        return new(scope with { ClientIds = clients }, facets, series, matrix, raw.Count, freshness);
    }

    private static MeasurementPopulation SelectViewPopulation(
        List<ReviewerPerformanceDailyCount> cells,
        ReviewerPerformanceViewQuery scope,
        ReviewerPerformanceRepositoryMatcher.Selection repositories)
    {
        var selected = new List<ReviewerPerformanceDailyCount>();
        var population = new List<ReviewerPerformanceDailyCount>();
        foreach (var row in cells)
        {
            var match = repositories.Match(row);
            if (match == ReviewerPerformanceRepositoryMatcher.MatchKind.None)
            {
                continue;
            }

            population.Add(row);
            if (match == ReviewerPerformanceRepositoryMatcher.MatchKind.Exact && ReviewerPerformanceDimensions.MatchesEvidenceDimensions(row, scope))
            {
                selected.Add(row);
            }
        }

        return new(selected, population);
    }

    private static bool RequiresRepositoryOrClientScope(ReviewerPerformanceQuery query)
    {
        return query.Grouping is "client" or "repository"
               || query.Breakdown?.Rows is "client" or "repository"
               || query.Breakdown?.Columns is "client" or "repository";
    }

    private static List<ReviewerPerformanceSeries> BuildSeries(
        ReviewerPerformanceQuery query,
        ReviewerPerformanceViewQuery scope,
        List<ReviewerPerformanceDailyCount> selected,
        List<ReviewerPerformanceDailyCount> measurementSelected,
        List<ReviewerPerformanceDailyCount> measurementPopulation,
        List<DateOnly> dates,
        ReviewerPerformanceLabelResolver labels,
        ReviewerPerformanceRepositoryMatcher repositoryMatcher)
    {
        var keys = SeriesKeys(selected, query.Grouping);
        ValidateSeriesSize(keys.Length, dates.Count);
        var series = new List<ReviewerPerformanceSeries>();
        foreach (var key in keys)
        {
            var group = SelectSeriesPopulation(measurementSelected, measurementPopulation, query.Grouping, key.Id, repositoryMatcher);
            var points = new List<ReviewerPerformancePoint>();
            foreach (var date in dates)
            {
                var window = SeriesWindow(query, scope, date);
                points.Add(MeasureWindow(query, scope, group, date, window, query.Grouping));
            }

            series.Add(new(key.Id, labels.Label(key, query.Grouping).Label, points));
        }

        return series;
    }

    private static ReviewerPerformanceFacet[] SeriesKeys(List<ReviewerPerformanceDailyCount> cells, string grouping)
    {
        if (grouping == "none")
        {
            return [new("all", "All selected evidence")];
        }

        var evidence = DimensionEvidence(cells, grouping).ToList();
        return ReviewerPerformanceDimensions.FacetItems(evidence, grouping).OrderBy(key => key.Id).ToArray();
    }

    private static void ValidateSeriesSize(int seriesCount, int dateCount)
    {
        if (seriesCount > ReviewerPerformanceRangeReader.MaximumSeries)
        {
            throw new ArgumentException("The selection exceeds 24 series. Narrow the selection or choose another grouping.");
        }

        if (seriesCount * dateCount > ReviewerPerformanceRangeReader.MaximumPoints)
        {
            throw new ArgumentException("The selection exceeds 2000 measurement points. Use wider buckets or fewer series.");
        }
    }

    private static MeasurementPopulation SelectSeriesPopulation(
        List<ReviewerPerformanceDailyCount> selected,
        List<ReviewerPerformanceDailyCount> population,
        string grouping,
        string id,
        ReviewerPerformanceRepositoryMatcher repositoryMatcher)
    {
        var clientId = grouping == "client" ? Guid.Parse(id) : (Guid?)null;
        var repositories = grouping == "repository" ? repositoryMatcher.Prepare([id]) : null;

        bool InGroup(ReviewerPerformanceDailyCount row) => clientId is { } value
            ? row.ClientId == value
            : repositories?.Matches(row, includeUnknownScope: false) ?? HasDimensionKey(row, grouping, id);

        var grouped = grouping == "none" ? selected : selected.Where(InGroup).ToList();
        if (grouping is not ("client" or "repository"))
        {
            return new(grouped, population);
        }

        var groupedRepositories = grouping == "repository"
            ? grouped.Select(row => (row.ClientId, row.RepositoryId)).ToHashSet()
            : null;
        var broader = population.Where(row => InGroup(row) ||
                                              (grouping == "repository" && row.IsMiss && row.ProviderScope.Length == 0
                                               && groupedRepositories!.Contains((row.ClientId, row.RepositoryId)))).ToList();
        return new(grouped, broader);
    }

    private static MeasurementWindow SeriesWindow(ReviewerPerformanceQuery query, ReviewerPerformanceViewQuery scope, DateOnly date)
    {
        var from = query.Aggregation == "cumulative" ? scope.From : date;
        return new(from, ReviewerPerformanceBuckets.BucketEnd(date, query.Bucket));
    }

    private static ReviewerPerformancePoint MeasureWindow(
        ReviewerPerformanceQuery query,
        ReviewerPerformanceViewQuery scope,
        MeasurementPopulation population,
        DateOnly date,
        MeasurementWindow window,
        string grouping)
    {
        var evidence = WithinWindow(population.Selected, window);
        var broader = WithinWindow(population.Broader, window);
        var measurement = ReviewerPerformanceMeasurement.Measure(date, evidence, broader, scope, grouping) with
        {
            WindowFrom = window.From < scope.From ? scope.From : window.From,
            WindowTo = window.To > scope.To ? scope.To : window.To
        };
        if (query.Aggregation == "period" && !HasPeriodObservation(population.Selected, date, window.To))
        {
            return ReviewerPerformanceMeasurement.WithoutPeriodObservation(measurement);
        }

        return measurement;
    }

    private static List<ReviewerPerformanceMatrixCell> BuildMatrix(
        ReviewerPerformanceQuery query,
        ReviewerPerformanceViewQuery scope,
        int viewIndex,
        List<ReviewerPerformanceDailyCount> selected,
        List<ReviewerPerformanceDailyCount> population,
        List<DateOnly> dates,
        ReviewerPerformanceLabelResolver labels,
        ReviewerPerformanceRepositoryMatcher repositoryMatcher)
    {
        var matrix = new List<ReviewerPerformanceMatrixCell>();
        if (query.Breakdown is not { } breakdown || breakdown.ViewIndex != viewIndex)
        {
            return matrix;
        }

        var date = MatrixDate(query, scope, breakdown, dates);
        var window = MatrixWindow(query, scope, date);
        var matrixSelected = WithinWindow(selected, window);
        var matrixPopulation = WithinWindow(population, window);
        var rows = AxisKeys(matrixSelected, breakdown.Rows);
        var columns = AxisKeys(matrixSelected, breakdown.Columns);
        ValidateMatrixSize(rows.Count, columns.Count);
        var rowRepositories = PrepareAxisRepositorySelections(rows, breakdown.Rows, repositoryMatcher);
        var columnRepositories = PrepareAxisRepositorySelections(columns, breakdown.Columns, repositoryMatcher);
        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                var intersection = SelectAxisIntersection(
                    matrixSelected, matrixPopulation, breakdown, row.Id, column.Id,
                    rowRepositories?.GetValueOrDefault(row.Id), columnRepositories?.GetValueOrDefault(column.Id));
                var dimensionalScope = DimensionalScope(scope, breakdown, row.Id, column.Id);
                // Matrix populations have already been restricted to the intersecting view window.
                var measurement = ReviewerPerformanceMeasurement.Measure(date, intersection.Selected, intersection.Broader, dimensionalScope, "none") with
                {
                    WindowFrom = window.From,
                    WindowTo = window.To
                };
                if (query.Aggregation == "period" && !HasPeriodObservation(intersection.Selected, date, window.To))
                {
                    measurement = ReviewerPerformanceMeasurement.WithoutPeriodObservation(measurement);
                }

                matrix.Add(
                    new(
                        row.Id, labels.Label(row, breakdown.Rows).Label,
                        column.Id, labels.Label(column, breakdown.Columns).Label,
                        measurement));
            }
        }

        return matrix;
    }

    private static DateOnly MatrixDate(
        ReviewerPerformanceQuery query, ReviewerPerformanceViewQuery scope, ReviewerPerformanceBreakdownQuery breakdown, List<DateOnly> dates)
    {
        var measurementDate = breakdown.Date ?? scope.To;
        var date = ReviewerPerformanceBuckets.Dates(measurementDate, measurementDate, query.Bucket).First();
        if (!dates.Contains(date))
        {
            throw new ArgumentException("The matrix bucket does not intersect its view window.");
        }

        return date;
    }

    private static MeasurementWindow MatrixWindow(ReviewerPerformanceQuery query, ReviewerPerformanceViewQuery scope, DateOnly date)
    {
        var from = query.Aggregation == "cumulative" ? scope.From : date;
        var to = ReviewerPerformanceBuckets.BucketEnd(date, query.Bucket);
        return new(from < scope.From ? scope.From : from, to > scope.To ? scope.To : to);
    }

    private static List<ReviewerPerformanceFacet> AxisKeys(List<ReviewerPerformanceDailyCount> cells, string dimension)
    {
        return DimensionEvidence(cells, dimension)
            .SelectMany(row => ReviewerPerformanceDimensions.Keys(row, dimension))
            .DistinctBy(key => key.Id)
            .OrderBy(key => key.Id)
            .ToList();
    }

    private static IEnumerable<ReviewerPerformanceDailyCount> DimensionEvidence(List<ReviewerPerformanceDailyCount> cells, string dimension)
    {
        return cells.Where(row => !ReviewerPerformanceMeasurement.IsCoverage(row) && (dimension != "model" || !row.IsMiss));
    }

    private static void ValidateMatrixSize(int rows, int columns)
    {
        if (rows > MaximumMatrixAxisValues || columns > MaximumMatrixAxisValues || rows * columns > MaximumMatrixCells)
        {
            throw new ArgumentException("The dimension matrix exceeds 12 values per axis. Narrow the selection.");
        }
    }

    private static MeasurementPopulation SelectAxisIntersection(
        List<ReviewerPerformanceDailyCount> selected,
        List<ReviewerPerformanceDailyCount> population,
        ReviewerPerformanceBreakdownQuery breakdown,
        string rowId,
        string columnId,
        ReviewerPerformanceRepositoryMatcher.Selection? rowRepositories,
        ReviewerPerformanceRepositoryMatcher.Selection? columnRepositories)
    {
        var evidence = selected.Where(item => MatchesAxisEvidence(item, breakdown.Rows, rowId, rowRepositories)
                                              && MatchesAxisEvidence(item, breakdown.Columns, columnId, columnRepositories)).ToList();
        var broader = population.Where(item => InAxisPopulation(item, breakdown.Rows, rowId, rowRepositories)
                                               && InAxisPopulation(item, breakdown.Columns, columnId, columnRepositories)).ToList();
        return new(evidence, broader);
    }

    private static Dictionary<string, ReviewerPerformanceRepositoryMatcher.Selection>? PrepareAxisRepositorySelections(
        List<ReviewerPerformanceFacet> keys, string dimension, ReviewerPerformanceRepositoryMatcher repositoryMatcher)
    {
        if (dimension != "repository")
        {
            return null;
        }

        return keys.ToDictionary(key => key.Id, key => repositoryMatcher.Prepare([key.Id]), StringComparer.Ordinal);
    }

    private static bool MatchesAxisEvidence(
        ReviewerPerformanceDailyCount row, string dimension, string id, ReviewerPerformanceRepositoryMatcher.Selection? repositories)
    {
        return repositories?.Matches(row, includeUnknownScope: false) ?? HasDimensionKey(row, dimension, id);
    }

    private static bool HasDimensionKey(ReviewerPerformanceDailyCount row, string dimension, string id)
    {
        return ReviewerPerformanceDimensions.Keys(row, dimension).Any(key => key.Id == id);
    }

    private static bool InAxisPopulation(
        ReviewerPerformanceDailyCount row, string dimension, string id, ReviewerPerformanceRepositoryMatcher.Selection? repositories) => dimension switch
    {
        "client" => HasDimensionKey(row, dimension, id),
        "repository" => repositories!.Matches(row, includeUnknownScope: true),
        _ => true
    };

    private static ReviewerPerformanceViewQuery DimensionalScope(
        ReviewerPerformanceViewQuery scope, ReviewerPerformanceBreakdownQuery breakdown, string rowId, string columnId)
    {
        IReadOnlyList<string>? Selection(string dimension, IReadOnlyList<string>? fallback)
        {
            if (breakdown.Rows == dimension)
            {
                return [rowId];
            }

            if (breakdown.Columns == dimension)
            {
                return [columnId];
            }

            return fallback;
        }

        return scope with
        {
            Types = Selection("type", scope.Types),
            Qualifiers = Selection("qualifier", scope.Qualifiers),
            Models = Selection("model", scope.Models),
            Repositories = Selection("repository", scope.Repositories)
        };
    }

    private static List<ReviewerPerformanceDailyCount> WithinWindow(List<ReviewerPerformanceDailyCount> cells, MeasurementWindow window)
    {
        return cells.Where(row => row.BucketDate >= window.From && row.BucketDate <= window.To).ToList();
    }

    private static bool HasPeriodObservation(List<ReviewerPerformanceDailyCount> cells, DateOnly from, DateOnly to)
    {
        return cells.Any(row => !ReviewerPerformanceMeasurement.IsCoverage(row) && row.BucketDate >= from && row.BucketDate <= to);
    }

    private sealed record MeasurementPopulation(List<ReviewerPerformanceDailyCount> Selected, List<ReviewerPerformanceDailyCount> Broader);

    private readonly record struct MeasurementWindow(DateOnly From, DateOnly To);
}
