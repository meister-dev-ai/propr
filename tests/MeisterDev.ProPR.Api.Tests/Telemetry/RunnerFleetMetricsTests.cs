// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.Metrics;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Telemetry;

/// <summary>
///     What the fleet metrics publish, and just as importantly what they do not. Metric labels reach
///     dashboards, alert payloads, and third-party backends, so a repository path or a client name that
///     leaks into one is far harder to take back than a log line.
/// </summary>
public sealed class RunnerFleetMetricsTests : IDisposable
{
    private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly MeterListener _listener = new();

    // Unique per test instance. Every metrics class in the product publishes on the same meter name, so a
    // listener filtering by that name alone also captures instruments other tests create beside this one.
    private readonly string _meterName = $"MeisterProPR.Test.{Guid.NewGuid():N}";

    public RunnerFleetMetricsTests()
    {
        this._listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == this._meterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        this._listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (this._measurements)
            {
                this._measurements.Add(
                    (
                        instrument.Name,
                        value,
                        tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });

        this._listener.Start();
    }

    public void Dispose()
    {
        this._listener.Dispose();
    }

    [Theory]
    [InlineData(ReviewJobReclaimOutcome.Requeued, "requeued")]
    [InlineData(ReviewJobReclaimOutcome.FailedOutOfReclaimBudget, "failed_out_of_budget")]
    [InlineData(ReviewJobReclaimOutcome.NotReclaimed, "not_reclaimed")]
    public void AReclaim_IsCountedWithWhatHappenedToTheJob(ReviewJobReclaimOutcome outcome, string expectedTag)
    {
        using var metrics = new RunnerFleetMetrics(EmptyScopeFactory(), this._meterName);

        metrics.RecordReclaim(outcome);

        var reclaim = this.Single("review_lease_reclaims_total");
        Assert.Equal(1, reclaim.Value);
        Assert.Equal(expectedTag, reclaim.Tags["reclaim_outcome"]);
    }

    // An expiry and a reclaim answer different questions: how often leases are being lost, and what happened
    // to the jobs. A sweep that finds an expired lease another host already took counts for the first.
    [Fact]
    public void AnExpiredLeaseNobodyCouldReclaim_StillCountsAsAnExpiry()
    {
        using var metrics = new RunnerFleetMetrics(EmptyScopeFactory(), this._meterName);

        metrics.RecordReclaim(ReviewJobReclaimOutcome.NotReclaimed);

        Assert.Equal(1, this.Single("review_lease_expiries_total").Value);
    }

    [Fact]
    public void ASlotRefusal_IsCountedWithItsReason()
    {
        using var metrics = new RunnerFleetMetrics(EmptyScopeFactory(), this._meterName);

        metrics.RecordSlotRefusal(RunnerLeaseRefusal.SlotLimitReached);

        var refusal = this.Single("review_runner_slot_refusals_total");
        Assert.Equal(1, refusal.Value);
        Assert.Equal("SlotLimitReached", refusal.Tags["refusal"]);
    }

    // The label values are mapped rather than emitted as enum names, so renaming a C# member cannot break a
    // dashboard that has been filtering on the old value.
    [Fact]
    public void ReclaimOutcomeLabels_AreStableRatherThanTheEnumMemberNames()
    {
        using var metrics = new RunnerFleetMetrics(EmptyScopeFactory(), this._meterName);

        metrics.RecordReclaim(ReviewJobReclaimOutcome.FailedOutOfReclaimBudget);

        Assert.Equal("failed_out_of_budget", this.Single("review_lease_reclaims_total").Tags["reclaim_outcome"]);
    }

    [Fact]
    public void NoMeasurement_CarriesAnythingIdentifyingACustomer()
    {
        using var metrics = new RunnerFleetMetrics(EmptyScopeFactory(), this._meterName);

        metrics.RecordReclaim(ReviewJobReclaimOutcome.Requeued);
        metrics.RecordSlotRefusal(RunnerLeaseRefusal.SlotLimitReached);

        // Values as well as keys. Checking only the keys would pass an implementation that put a
        // repository path or a token into a tag value under a name like "detail".
        string[] forbidden = ["repository", "path", "client_name", "display_name", "credential", "token", "url"];
        string[] allowedKeys = ["reclaim_outcome", "refusal", "stall_cause"];

        foreach (var measurement in this.All())
        {
            foreach (var (key, value) in measurement.Tags)
            {
                Assert.Contains(key, allowedKeys);
                Assert.DoesNotContain(forbidden, f => key.Contains(f, StringComparison.OrdinalIgnoreCase));

                // Every emitted value is a fixed vocabulary term, so anything that parses as a path, a
                // URL, or a long opaque string is by definition not one of ours.
                var text = value?.ToString() ?? string.Empty;
                Assert.DoesNotContain('/', text);
                Assert.DoesNotContain('\\', text);
                Assert.True(text.Length <= 48, $"Tag {key} carries an unexpectedly long value.");
            }
        }
    }

    private (string Instrument, long Value, Dictionary<string, object?> Tags) Single(string instrument)
    {
        this._listener.RecordObservableInstruments();
        lock (this._measurements)
        {
            return Assert.Single(this._measurements.Where(m => m.Instrument == instrument));
        }
    }

    private IReadOnlyList<(string Instrument, long Value, Dictionary<string, object?> Tags)> All()
    {
        lock (this._measurements)
        {
            return [.. this._measurements];
        }
    }

    /// <summary>A scope factory whose container has neither collaborator, which is the degraded case.</summary>
    private static IServiceScopeFactory EmptyScopeFactory()
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IRunnerFleetMonitor)).Returns((IRunnerFleetMonitor?)null);
        provider.GetService(typeof(IRunnerLeaseOfferStore)).Returns((IRunnerLeaseOfferStore?)null);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }
}
