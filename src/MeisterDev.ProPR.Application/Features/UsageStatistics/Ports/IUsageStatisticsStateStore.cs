// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;

/// <summary>Reads and writes the installation's usage-statistics identity, preference and send history.</summary>
public interface IUsageStatisticsStateStore
{
    /// <summary>Returns the persisted state, creating the installation identity on first use.</summary>
    Task<UsageStatisticsState> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the community toggle. Effective immediately, and left alone by edition changes.</summary>
    Task<UsageStatisticsState> SetCommunityOptInAsync(
        bool optIn,
        Guid? actorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Records that an administrator was shown what is sent. Idempotent.</summary>
    Task<UsageStatisticsState> RecordConsentGateSatisfiedAsync(CancellationToken cancellationToken = default);

    /// <summary>Records that the notice was dismissed, which hides it without changing what is sent.</summary>
    Task<UsageStatisticsState> RecordNoticeDismissedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Claims today's send by moving the attempt timestamp forward, and reports whether this caller won it.
    ///     <para>
    ///         A single conditional update rather than a read followed by a write, so two replicas that wake at
    ///         the same moment cannot both decide they are due. The timestamp moves before the request goes
    ///         out, so a process that dies mid-send does not send again on its next start.
    ///     </para>
    /// </summary>
    /// <param name="notBefore">The attempt timestamp a stored value must predate for the claim to succeed.</param>
    /// <param name="claimedAt">The timestamp to store when the claim succeeds.</param>
    Task<bool> TryClaimSendAsync(
        DateTimeOffset notBefore,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the outcome of one send attempt and any version or advisory information it returned.</summary>
    Task<UsageStatisticsState> RecordSendOutcomeAsync(
        UsageStatisticsSendOutcome outcome,
        CancellationToken cancellationToken = default);
}
