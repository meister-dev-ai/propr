// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

/// <summary>
///     The usage-statistics state the installation persists.
///     <para>
///         The community opt-in and the effective state are stored separately. A commercial license activates
///         sending without overwriting the stored community preference, so removing the license returns control
///         to the toggle at its last stored value.
///     </para>
/// </summary>
/// <param name="InstanceId">The installation's random identifier.</param>
/// <param name="CommunityOptIn">The community toggle. On by default, and ignored while a license is installed.</param>
/// <param name="ConsentGateSatisfiedAt">
///     When an administrator was first shown what is sent. Null means nothing is sent.
/// </param>
/// <param name="NoticeDismissedAt">
///     When an administrator dismissed the notice. Dismissal hides the notice and changes nothing else.
/// </param>
/// <param name="LastAttemptAt">When the last send was attempted, successful or not.</param>
/// <param name="LastAttemptSucceeded">Whether that attempt reached the receiver.</param>
/// <param name="LastAttemptDetail">A short operator-facing description of the outcome.</param>
/// <param name="LastSuccessAt">When a snapshot last reached the receiver. Also the start of the next window.</param>
/// <param name="LatestVersion">The newest release the receiver reported.</param>
/// <param name="Advisories">Advisories the receiver reported.</param>
/// <param name="UpdateInformationReceivedAt">When the version and advisory information arrived.</param>
public sealed record UsageStatisticsState(
    Guid InstanceId,
    bool CommunityOptIn,
    DateTimeOffset? ConsentGateSatisfiedAt,
    DateTimeOffset? NoticeDismissedAt,
    DateTimeOffset? LastAttemptAt,
    bool? LastAttemptSucceeded,
    string? LastAttemptDetail,
    DateTimeOffset? LastSuccessAt,
    string? LatestVersion,
    IReadOnlyList<ProductAdvisory> Advisories,
    DateTimeOffset? UpdateInformationReceivedAt)
{
    /// <summary>Whether an administrator has been shown what is sent.</summary>
    public bool IsConsentGateSatisfied => this.ConsentGateSatisfiedAt.HasValue;

    /// <summary>
    ///     Whether this installation currently sends, given its edition.
    ///     <para>
    ///         Commercial installations send while a license is installed; the community toggle governs
    ///         otherwise. In both cases nothing is sent until the consent gate is satisfied.
    ///     </para>
    /// </summary>
    public bool IsSendingEnabled(UsageStatisticsEdition edition)
    {
        return this.IsConsentGateSatisfied
               && (edition == UsageStatisticsEdition.Commercial || this.CommunityOptIn);
    }
}
