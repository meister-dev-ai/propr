// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

/// <summary>
///     Turns a raw count into the bucket label that leaves the installation.
///     <para>
///         The boundaries are part of the published payload documentation, so changing one changes what the
///         documentation states is collected. Update the documentation together with any change here.
///     </para>
/// </summary>
public static class UsageStatisticsBuckets
{
    /// <summary>Bucket labels for the account count, in ascending order.</summary>
    public static readonly IReadOnlyList<string> ActiveUserLabels = ["1", "2-5", "6-20", "21-50", "50+"];

    /// <summary>Bucket labels for reviewed pull requests per week, in ascending order.</summary>
    public static readonly IReadOnlyList<string> PullRequestLabels = ["0", "1-20", "21-100", "101-500", "500+"];

    /// <summary>Bucket labels for finding counts per week, in ascending order.</summary>
    public static readonly IReadOnlyList<string> FindingLabels = ["0", "1-50", "51-250", "251-1000", "1000+"];

    /// <summary>
    ///     Buckets the number of accounts that can sign in.
    ///     <para>
    ///         There is no zero bucket: an installation an administrator can sign into has at least one
    ///         account, so a count below one is reported as one rather than adding a sixth label.
    ///     </para>
    /// </summary>
    public static string ForActiveUsers(int count)
    {
        return count switch
        {
            <= 1 => "1",
            <= 5 => "2-5",
            <= 20 => "6-20",
            <= 50 => "21-50",
            _ => "50+",
        };
    }

    /// <summary>Buckets reviewed pull requests, after normalising the observation window to one week.</summary>
    public static string ForWeeklyPullRequests(double perWeek)
    {
        return RoundToWholeCount(perWeek) switch
        {
            <= 0 => "0",
            <= 20 => "1-20",
            <= 100 => "21-100",
            <= 500 => "101-500",
            _ => "500+",
        };
    }

    /// <summary>Buckets findings, after normalising the observation window to one week.</summary>
    public static string ForWeeklyFindings(double perWeek)
    {
        return RoundToWholeCount(perWeek) switch
        {
            <= 0 => "0",
            <= 50 => "1-50",
            <= 250 => "51-250",
            <= 1000 => "251-1000",
            _ => "1000+",
        };
    }

    /// <summary>
    ///     Rounds a normalised rate to a whole count before bucketing.
    ///     <para>
    ///         Bucketing the rounded whole number keeps the published boundaries whole counts rather than
    ///         fractional rates.
    ///     </para>
    /// </summary>
    private static long RoundToWholeCount(double perWeek)
    {
        if (double.IsNaN(perWeek) || perWeek <= 0)
        {
            return 0;
        }

        return perWeek >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Round(perWeek, MidpointRounding.AwayFromZero);
    }
}
