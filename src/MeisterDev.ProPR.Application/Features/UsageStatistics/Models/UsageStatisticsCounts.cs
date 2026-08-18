// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     Raw counts read from the installation's own tables, before bucketing.
///     <para>
///         These numbers stay inside the installation. They are converted to bucket labels while the snapshot
///         is built, and no wire type carries them.
///     </para>
/// </summary>
/// <param name="ActiveUserAccounts">Accounts that can currently sign in. A point-in-time count.</param>
/// <param name="PullRequestsReviewed">Distinct pull requests with a completed review in the window.</param>
/// <param name="FindingsRaised">Findings posted on pull requests in the window.</param>
/// <param name="FindingsAccepted">
///     Findings the author addressed or acknowledged, or <see langword="null" /> when the installation records
///     no finding outcomes at all.
/// </param>
/// <param name="FindingsDismissed">
///     Findings the author dismissed, or <see langword="null" /> under the same condition as
///     <paramref name="FindingsAccepted" />.
/// </param>
public sealed record UsageStatisticsCounts(
    int ActiveUserAccounts,
    int PullRequestsReviewed,
    int FindingsRaised,
    int? FindingsAccepted,
    int? FindingsDismissed);
