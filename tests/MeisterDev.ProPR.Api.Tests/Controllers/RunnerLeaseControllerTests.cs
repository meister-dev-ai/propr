// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.Metrics;
using System.Security.Claims;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     What a runner is told when it is offered no work, and what an operator is told about the same
///     answer. The two differ on purpose: a runner that gets no work should keep polling, so several
///     different reasons reach it as the same empty answer, and the counters are what separate them again.
/// </summary>
public sealed class RunnerLeaseControllerTests : IDisposable
{
    private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = [];
    private readonly MeterListener _listener = new();

    // Unique per test instance. Every metrics class in the product publishes on one meter name, so a
    // listener filtering by that name alone also captures instruments other tests create beside it.
    private readonly string _meterName = $"MeisterProPR.Test.{Guid.NewGuid():N}";

    public RunnerLeaseControllerTests()
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
                this._measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value)));
            }
        });

        this._listener.Start();
    }

    public void Dispose()
    {
        this._listener.Dispose();
    }

    // Answered the same way a quiet queue is, because the ceiling clears when a running review finishes and
    // a runner has nothing to do differently in the meantime. Backing it off, or answering it as an error,
    // would delay the first job it picks up once the ceiling clears.
    [Fact]
    public async Task ACeilingRefusal_IsAnsweredWithNoContent()
    {
        using var metrics = this.CreateMetrics();
        var controller = CreateController(
            RunnerLeaseOffer.RefuseAtConcurrencyCeiling(3, "This installation is running 3 of 3 concurrent reviews."),
            metrics);

        var result = await controller.Lease(
            new RunnerLeaseHttpRequest { FreeSlots = 1, ContractVersion = RunnerContractVersion.Current }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
    }

    // The runner cannot tell a full ceiling from an empty queue, so this counter is the only place the
    // difference survives. Without it an installation held at its limit reads as an idle one.
    [Fact]
    public async Task ACeilingRefusal_IsCountedAgainstTheCeilingItReached()
    {
        using var metrics = this.CreateMetrics();
        var controller = CreateController(RunnerLeaseOffer.RefuseAtConcurrencyCeiling(3, "at the ceiling"), metrics);

        await controller.Lease(new RunnerLeaseHttpRequest { FreeSlots = 1, ContractVersion = RunnerContractVersion.Current }, CancellationToken.None);

        var refusal = this.Single("review_runner_ceiling_refusals_total");
        Assert.Equal(1, refusal.Value);
        Assert.Equal(3, Assert.IsType<int>(refusal.Tags["ceiling"]));
    }

    [Fact]
    public async Task AnEmptyQueue_IsAnsweredWithNoContentAndCountsNothing()
    {
        using var metrics = this.CreateMetrics();
        var controller = CreateController(RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.NoMatchingWork), metrics);

        var result = await controller.Lease(
            new RunnerLeaseHttpRequest { FreeSlots = 1, ContractVersion = RunnerContractVersion.Current }, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(this.All());
    }

    // An unlicensed installation is the one lease refusal that is a standing operator decision rather than
    // a transient condition, so it is the one the runner is told about rather than left to infer.
    [Fact]
    public async Task AnUnlicensedInstallation_IsRefusedWithTheContractCodeAndCounted()
    {
        using var metrics = this.CreateMetrics();
        var controller = CreateController(
            RunnerLeaseOffer.Refuse(RunnerLeaseRefusal.NotLicensed, "Distributed review execution is not licensed for this installation."),
            metrics);

        var result = await controller.Lease(
            new RunnerLeaseHttpRequest { FreeSlots = 1, ContractVersion = RunnerContractVersion.Current }, CancellationToken.None);

        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        var error = Assert.IsType<RunnerContractError>(refused.Value);
        Assert.Equal(RunnerContractError.SlotLimitReached, error.Code);
        Assert.Equal("Distributed review execution is not licensed for this installation.", error.Message);

        var counted = this.Single("review_runner_slot_refusals_total");
        Assert.Equal(1, counted.Value);
        Assert.Equal("NotLicensed", counted.Tags["refusal"]);
    }

    private static RunnerLeaseController CreateController(RunnerLeaseOffer offer, RunnerFleetMetrics metrics)
    {
        var offers = Substitute.For<IRunnerLeaseOfferService>();
        offers.OfferAsync(Arg.Any<RunnerLeaseRequest>(), Arg.Any<CancellationToken>()).Returns(offer);

        var controller = new RunnerLeaseController(
            offers,
            Substitute.For<IReviewJobLeaseStore>(),
            Substitute.For<IRunnerCallAuthorizer>(),
            Options.Create(new ReviewLeaseOptions()),
            Substitute.For<IRunnerJobBudgetRegistry>(),
            new RunnerRelayReplayCache(),
            new RunnerSubmissionLedger(),
            Substitute.For<IRunnerWorkspaceRegistry>(),
            metrics)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        // The runner's identity comes from the authenticated principal and never from the body, so a
        // request without the claim is answered 401 before any offer is made.
        var identity = new ClaimsIdentity(RunnerAuthenticationDefaults.Scheme);
        identity.AddClaim(new Claim(RunnerAuthenticationDefaults.RunnerIdClaim, Guid.NewGuid().ToString("D")));
        controller.HttpContext.User = new ClaimsPrincipal(identity);

        return controller;
    }

    private RunnerFleetMetrics CreateMetrics()
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IRunnerFleetMonitor)).Returns((IRunnerFleetMonitor?)null);
        provider.GetService(typeof(IRunnerLeaseOfferStore)).Returns((IRunnerLeaseOfferStore?)null);

        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);

        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);

        return new RunnerFleetMetrics(factory, this._meterName);
    }

    private (string Instrument, long Value, Dictionary<string, object?> Tags) Single(string instrument)
    {
        lock (this._measurements)
        {
            return Assert.Single(this._measurements, m => m.Instrument == instrument);
        }
    }

    private IReadOnlyList<(string Instrument, long Value, Dictionary<string, object?> Tags)> All()
    {
        lock (this._measurements)
        {
            return [.. this._measurements];
        }
    }
}
