// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Services;

/// <summary>
///     Runs one send cycle. This is the only place that decides to use the outbound path.
///     <para>
///         Every reason not to send is checked before the transport is used, so an installation that is off or
///         awaiting consent performs no request and resolves no name; the zero-egress test covers that. The
///         HTTP client is constructed when this class is resolved, which opens no socket and resolves no name.
///     </para>
/// </summary>
public sealed class UsageStatisticsSender(
    IUsageStatisticsStateStore stateStore,
    UsageStatisticsSnapshotBuilder snapshotBuilder,
    UsageStatisticsEditionResolver editionResolver,
    IUsageStatisticsPingClient pingClient,
    TimeProvider timeProvider)
{
    /// <summary>Runs one cycle and reports what it decided.</summary>
    public async Task<UsageStatisticsCycleResult> SendIfDueAsync(CancellationToken cancellationToken = default)
    {
        var state = await stateStore.GetAsync(cancellationToken);
        var edition = await editionResolver.ResolveAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        var decision = Decide(state, edition, now);
        if (decision != UsageStatisticsSendDecision.Sent)
        {
            return new UsageStatisticsCycleResult(decision, state.LastAttemptAt);
        }

        // The claim moves the attempt timestamp before anything is sent, so two replicas that woke together
        // cannot both win it and a process that dies mid-send does not send again on its next start.
        if (!await stateStore.TryClaimSendAsync(now - UsageStatisticsSendSchedule.MinimumInterval, now, cancellationToken))
        {
            return new UsageStatisticsCycleResult(UsageStatisticsSendDecision.NotDue, now);
        }

        var snapshot = await snapshotBuilder.BuildAsync(state, edition, cancellationToken);
        var outcome = await pingClient.SendAsync(snapshot, cancellationToken);

        try
        {
            await stateStore.RecordSendOutcomeAsync(outcome, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The snapshot has already been sent. The outcome is only displayed on the settings page, so a
            // failed write must not leave the day unclaimed and cause a second send.
        }

        return new UsageStatisticsCycleResult(UsageStatisticsSendDecision.Sent, now);
    }

    /// <summary>
    ///     Decides whether this cycle sends.
    ///     <para>
    ///         The stored community preference is checked before consent so the settings page reports the state
    ///         the operator set rather than a pending consent gate.
    ///     </para>
    /// </summary>
    public static UsageStatisticsSendDecision Decide(
        UsageStatisticsState state,
        UsageStatisticsEdition edition,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (edition != UsageStatisticsEdition.Commercial && !state.CommunityOptIn)
        {
            return UsageStatisticsSendDecision.Disabled;
        }

        if (!state.IsConsentGateSatisfied)
        {
            return UsageStatisticsSendDecision.AwaitingConsent;
        }

        // A stored timestamp ahead of this replica's clock is treated as not due. Reading it as due would let
        // clock skew between two replicas produce a second snapshot each day.
        //
        // A failed attempt does not hold the interval. The claim moves the timestamp before the request is
        // made, so without this a receiver that answered 502 once cost a whole day and the only way back was
        // an UPDATE against the database. A snapshot that did not arrive cannot be a duplicate.
        if (state.LastAttemptAt is { } lastAttempt
            && state.LastAttemptSucceeded != false
            && now - lastAttempt < UsageStatisticsSendSchedule.MinimumInterval)
        {
            return UsageStatisticsSendDecision.NotDue;
        }

        return UsageStatisticsSendDecision.Sent;
    }
}
