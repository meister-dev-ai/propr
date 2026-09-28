// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;

/// <summary>Reads the client-scoped review summary used by the hosted customer dashboard.</summary>
public interface ICustomerDashboardReader
{
    /// <summary>
    /// Returns processing reviews and findings from completed review jobs whose completion time is in
    /// [<paramref name="windowEnd"/> minus 30 days, <paramref name="windowEnd"/>).
    /// </summary>
    Task<CustomerDashboard> GetAsync(Guid clientId, DateTimeOffset windowEnd, CancellationToken ct);
}

/// <summary>
/// Client review activity in a fixed 30-day window. The start is inclusive and the end is exclusive;
/// the finding count uses the completion time of each review job.
/// </summary>
public sealed record CustomerDashboard(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    long RecentFindingCount,
    IReadOnlyList<CustomerRunningReview> RunningReviews);

/// <summary>A review currently processing for the selected client.</summary>
public sealed record CustomerRunningReview(
    Guid Id,
    JobStatus Status,
    ScmProvider Provider,
    string Repository,
    int PullRequestNumber,
    DateTimeOffset StartedAt);
