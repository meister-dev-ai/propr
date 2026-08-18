// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Services;

/// <summary>
///     Builds the snapshot from the installation's own tables when it is requested.
///     <para>
///         There is no event stream and no accumulator. Every counter is a query against data the product
///         already stores for its own purposes, so turning usage statistics off leaves nothing to clean up.
///     </para>
/// </summary>
public sealed class UsageStatisticsSnapshotBuilder(
    IUsageStatisticsCountSource countSource,
    IProductVersionProvider productVersionProvider,
    TimeProvider timeProvider)
{
    /// <summary>The window used before a snapshot has ever been delivered.</summary>
    internal static readonly TimeSpan DefaultWindow = TimeSpan.FromDays(7);

    /// <summary>
    ///     The shortest window a rate is extrapolated from.
    ///     <para>
    ///         Normalising three hours of activity to a week multiplies it by 56, which reports a short burst
    ///         as a sustained workload. The floor bounds that error.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan MinimumWindow = TimeSpan.FromDays(1);

    /// <summary>
    ///     The longest window counted, so a long-dormant installation reports recent activity rather than an
    ///     average over its whole idle period.
    /// </summary>
    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(30);

    /// <summary>Builds the snapshot this installation would send at the current time.</summary>
    /// <param name="state">The persisted state, which supplies the identity and the window start.</param>
    /// <param name="edition">The edition to report, resolved by the caller so it is read once per cycle.</param>
    /// <param name="cancellationToken">Cancels the queries.</param>
    public async Task<UsageStatisticsSnapshot> BuildAsync(
        UsageStatisticsState state,
        UsageStatisticsEdition edition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var now = timeProvider.GetUtcNow();
        var window = ResolveWindow(state.LastSuccessAt, now);
        var counts = await countSource.CountAsync(now - window, now, cancellationToken);
        var weeks = window.TotalDays / 7d;

        return new UsageStatisticsSnapshot
        {
            SchemaVersion = UsageStatisticsContract.SchemaVersion,
            InstanceId = state.InstanceId,
            ProductVersion = productVersionProvider.Version,
            Edition = edition,
            ActiveUsers = UsageStatisticsBuckets.ForActiveUsers(counts.ActiveUserAccounts),
            PullRequestsPerWeek = UsageStatisticsBuckets.ForWeeklyPullRequests(counts.PullRequestsReviewed / weeks),
            FindingsRaisedPerWeek = UsageStatisticsBuckets.ForWeeklyFindings(counts.FindingsRaised / weeks),
            FindingsAcceptedPerWeek = counts.FindingsAccepted is { } accepted
                ? UsageStatisticsBuckets.ForWeeklyFindings(accepted / weeks)
                : null,
            FindingsDismissedPerWeek = counts.FindingsDismissed is { } dismissed
                ? UsageStatisticsBuckets.ForWeeklyFindings(dismissed / weeks)
                : null,
        };
    }

    /// <summary>Resolves the observation window, bounded at both ends so a rate describes recent activity.</summary>
    internal static TimeSpan ResolveWindow(DateTimeOffset? lastSuccessAt, DateTimeOffset now)
    {
        if (lastSuccessAt is not { } since || since >= now)
        {
            return DefaultWindow;
        }

        var elapsed = now - since;
        if (elapsed < MinimumWindow)
        {
            return MinimumWindow;
        }

        return elapsed > MaximumWindow ? MaximumWindow : elapsed;
    }
}
