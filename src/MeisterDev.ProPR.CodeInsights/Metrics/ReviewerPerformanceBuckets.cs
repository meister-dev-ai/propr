// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.


namespace MeisterDev.ProPR.CodeInsights.Metrics;

internal static class ReviewerPerformanceBuckets
{
    internal static IEnumerable<DateOnly> Dates(DateOnly from, DateOnly to, string bucket)
    {
        var current = bucket switch
        {
            "week" => from.AddDays(-((int)from.DayOfWeek + 6) % 7),
            "month" => new(from.Year, from.Month, 1),
            _ => from
        };
        while (current <= to)
        {
            yield return current;
            current = bucket switch
            {
                "week" => current.AddDays(7),
                "month" => current.AddMonths(1),
                _ => current.AddDays(1)
            };
        }
    }

    internal static DateOnly BucketEnd(DateOnly date, string bucket) =>
        bucket switch
        {
            "week" => date.AddDays(6),
            "month" => date.AddMonths(1).AddDays(-1),
            _ => date
        };
}
