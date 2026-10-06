// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.CodeInsights.Http;
using static MeisterDev.ProPR.CodeInsights.Metrics.ReviewerPerformanceRangeReader;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceQueryValidator
{
    private const int MaximumSelectionValues = 128;
    private const int MaximumDimensionIdentityCharacters = 2048;
    private static readonly string[] Dimensions = ["client", "repository", "model", "type", "qualifier"];

    internal static void Validate(ReviewerPerformanceQuery query, IReadOnlyCollection<Guid> clients)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Views is null || query.Views.Count is < 1 or > 2)
        {
            throw new ArgumentException("Provide one or two views.");
        }

        if (query.Bucket is not ("day" or "week" or "month") || query.Aggregation is not ("cumulative" or "period") ||
            (query.Grouping != "none" && !Dimensions.Contains(query.Grouping)))
        {
            throw new ArgumentException("Unsupported bucket, aggregation or grouping.");
        }

        if (query.Breakdown is { } selectedBreakdown && (selectedBreakdown.ViewIndex < 0 || selectedBreakdown.ViewIndex >= query.Views.Count))
        {
            throw new ArgumentException("The breakdown view is outside the query.");
        }

        if (query.Breakdown is { } axes && (!Dimensions.Contains(axes.Rows) || !Dimensions.Contains(axes.Columns) || axes.Rows == axes.Columns))
        {
            throw new ArgumentException("Choose two different supported dimensions.");
        }

        foreach (var scope in query.Views)
        {
            if (scope is null)
            {
                throw new ArgumentException("A view cannot be null.");
            }

            if (scope.From.Year < 1900 || scope.To.Year > 9998)
            {
                throw new ArgumentException("Dates must be between 1900 and 9998.");
            }

            if (scope.To < scope.From || scope.To.DayNumber - scope.From.DayNumber >= MaximumDays)
            {
                throw new ArgumentException("The date window must be between 1 and 366 days.");
            }

            if (scope.ClientIds?.Any(id => !clients.Contains(id)) == true)
            {
                throw new UnauthorizedAccessException("A selected client is outside the authorized scope.");
            }

            bool Invalid(IReadOnlyList<string>? items, int maximumLength) =>
                items?.Count > MaximumSelectionValues || items?.Any(item => item is null || item.Length > maximumLength) == true;

            if (scope.ClientIds?.Count > MaximumSelectionValues || Invalid(scope.Repositories, MaximumRepositoryIdentityLength) ||
                Invalid(scope.Models, MaximumModelIdentityLength)
                || Invalid(scope.Types, MaximumDimensionIdentityCharacters) || Invalid(scope.Qualifiers, MaximumDimensionIdentityCharacters))
            {
                throw new ArgumentException("Selections exceed the query limits.");
            }
        }
    }
}
