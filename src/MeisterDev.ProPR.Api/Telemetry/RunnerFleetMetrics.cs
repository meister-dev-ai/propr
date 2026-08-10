// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Api.Telemetry;

/// <summary>
///     The numbers an operator needs to size a runner pool, diagnose a stall, or let an orchestrator scale
///     the fleet.
///     <para>
///         On the same meter as the rest of the review metrics on purpose. A separate meter would need its
///         own exporter configuration, and a metric an installation has to opt into is a metric nobody has
///         when they need it.
///     </para>
///     <para>
///         Every dimension here is a count, a cause, or a provider name. Deliberately no repository path,
///         no client display name, no runner display name, and nothing derived from a credential: metric
///         labels end up in dashboards, alert payloads, and third-party backends, which is the last place a
///         customer's repository layout should appear.
///     </para>
/// </summary>
public sealed class RunnerFleetMetrics : IDisposable
{
    /// <summary>The meter every review metric shares, so nothing needs its own exporter configuration.</summary>
    internal const string DefaultMeterName = "MeisterProPR";

    private readonly Counter<long> _leaseReclaims;
    private readonly Counter<long> _leaseExpiries;
    private readonly Counter<long> _slotRefusals;
    private readonly Meter _meter;

    /// <summary>Creates the fleet instruments and starts observing the fleet.</summary>
    /// <param name="scopeFactory">Factory used to resolve the scoped fleet monitor for observations.</param>
    public RunnerFleetMetrics(IServiceScopeFactory scopeFactory)
        : this(scopeFactory, DefaultMeterName)
    {
    }

    /// <summary>
    ///     Creates the instruments on a named meter. The name is a parameter only so a test can listen to
    ///     one instance in isolation: every meter in this process shares the default name, so a listener
    ///     filtering by name alone picks up instruments belonging to other tests running beside it.
    /// </summary>
    /// <param name="scopeFactory">Factory used to resolve the scoped fleet monitor for observations.</param>
    /// <param name="meterName">Meter to publish on.</param>
    internal RunnerFleetMetrics(IServiceScopeFactory scopeFactory, string meterName)
    {
        this._meter = new Meter(meterName, "1.0.0");

        // Gauges rather than counters: an autoscaler wants to know how many runners are alive right now,
        // not how many have ever been seen.
        this._meter.CreateObservableGauge(
            "review_runner_active_count",
            () => Observe(scopeFactory, status => status.ActiveRunnerCount),
            "runners",
            "Runners currently counted as active: enrolled, unrevoked, contract-compatible, and heartbeating");

        this._meter.CreateObservableGauge(
            "review_lease_held_count",
            // Registered directly rather than through the fleet status: this gauge needs only the lease
            // count, and routing it through the monitor made it disappear whenever the monitor's query
            // failed and ran that query on every scrape for nothing.
            () => new[] { new Measurement<int>(CountHeldLeases(scopeFactory)) },
            "leases",
            "Review jobs currently held under a live lease");

        // Deliberately a gauge with a cause label rather than a boolean. A stalled queue and a busy one are
        // the same pending count, so the cause is the part that is actually actionable, and an alert rule
        // wants to fire on the condition rather than reconstruct it from two other series.
        this._meter.CreateObservableGauge(
            "review_queue_stalled",
            () => ObserveStall(scopeFactory),
            "queues",
            "1 when the queue has work no runner is taking, labelled with why");

        this._leaseReclaims = this._meter.CreateCounter<long>(
            "review_lease_reclaims_total",
            "reclaims",
            "Leases taken back from a holder that stopped renewing, by what happened to the job");

        this._leaseExpiries = this._meter.CreateCounter<long>(
            "review_lease_expiries_total",
            "leases",
            "Leases observed past their expiry, whether or not the job was reclaimable");

        this._slotRefusals = this._meter.CreateCounter<long>(
            "review_runner_slot_refusals_total",
            "refusals",
            "Lease requests refused because no entitled runner slot was free");
    }

    /// <summary>Records one expired lease and what reclaiming it did.</summary>
    /// <param name="outcome">What happened to the job.</param>
    public void RecordReclaim(ReviewJobReclaimOutcome outcome)
    {
        this._leaseExpiries.Add(1);
        this._leaseReclaims.Add(1, new TagList { { "reclaim_outcome", ToOutcomeTag(outcome) } });
    }

    /// <summary>Records one lease request refused for want of a slot.</summary>
    /// <param name="refusal">Which refusal it was.</param>
    public void RecordSlotRefusal(RunnerLeaseRefusal refusal)
    {
        this._slotRefusals.Add(1, new TagList { { "refusal", refusal.ToString() } });
    }

    /// <summary>Disposes the underlying meter.</summary>
    public void Dispose()
    {
        this._meter.Dispose();
    }

    /// <summary>
    ///     Reclaim outcomes as stable label values. Mapped rather than emitted as enum names so renaming a
    ///     C# member does not silently break somebody's dashboard.
    /// </summary>
    private static string ToOutcomeTag(ReviewJobReclaimOutcome outcome)
    {
        return outcome switch
        {
            ReviewJobReclaimOutcome.Requeued => "requeued",
            ReviewJobReclaimOutcome.FailedOutOfReclaimBudget => "failed_out_of_budget",
            _ => "not_reclaimed",
        };
    }

    private static IEnumerable<Measurement<int>> Observe(
        IServiceScopeFactory scopeFactory,
        Func<RunnerFleetStatus, int> select)
    {
        var status = ReadStatus(scopeFactory);
        return status is null ? [] : [new Measurement<int>(select(status))];
    }

    private static IEnumerable<Measurement<int>> ObserveStall(IServiceScopeFactory scopeFactory)
    {
        var status = ReadStatus(scopeFactory);
        if (status is null)
        {
            return [];
        }

        return status.Stall is null
            ? [new Measurement<int>(0, new KeyValuePair<string, object?>("stall_cause", "none"))]
            :
            [
                new Measurement<int>(
                    1,
                    new KeyValuePair<string, object?>("stall_cause", status.Stall.Cause.ToString()),
                    new KeyValuePair<string, object?>("pending_jobs", status.Stall.PendingJobCount)),
            ];
    }

    /// <summary>
    ///     Reads the fleet, returning null when it cannot be read. A metric callback that throws takes the
    ///     whole export with it, so an unreachable database costs one scrape rather than every series on it.
    /// </summary>
    private static RunnerFleetStatus? ReadStatus(IServiceScopeFactory scopeFactory)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var monitor = scope.ServiceProvider.GetService<IRunnerFleetMonitor>();
            return monitor?.GetStatusAsync().GetAwaiter().GetResult();
        }
#pragma warning disable CA1031 // One failed observation must not take the export down.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static int CountHeldLeases(IServiceScopeFactory scopeFactory)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var offers = scope.ServiceProvider.GetService<IRunnerLeaseOfferStore>();
            return offers is null ? 0 : offers.CountRunnersHoldingLeasesAsync().GetAwaiter().GetResult();
        }
#pragma warning disable CA1031 // Same reason as above.
        catch (Exception)
#pragma warning restore CA1031
        {
            return 0;
        }
    }
}
