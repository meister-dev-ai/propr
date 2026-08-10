// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Features.Reviewing.Runners;
using MeisterDev.ProPR.Api.Telemetry;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.AspNetCore.Authorization;
using MeisterDev.ProPR.Application.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Where a runner asks for work and keeps what it was given.
///     <para>
///         Thin like the execution surface, and for the same reason: the offer rules, the claim, and the
///         directives all live in services, so nothing here can grow a second opinion about who may hold
///         what. The runner's identity comes from its credential and never from the request.
///     </para>
/// </summary>
[ApiController]
[Route("runners")]
[Authorize(AuthenticationSchemes = RunnerAuthenticationDefaults.Scheme)]
public sealed class RunnerLeaseController(
    IRunnerLeaseOfferService offers,
    IReviewJobLeaseStore leases,
    IRunnerCallAuthorizer authorizer,
    IOptions<ReviewLeaseOptions> leaseOptions,
    IRunnerJobBudgetRegistry budgets,
    RunnerRelayReplayCache replays,
    RunnerSubmissionLedger submissions,
    IRunnerWorkspaceRegistry workspaces,
    RunnerFleetMetrics? metrics = null) : ControllerBase
{
    /// <summary>Asks for a job. Answered with a manifest, or with a typed reason there is none.</summary>
    /// <param name="request">How much room the runner has and which contract it speaks.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("lease")]
    [ProducesResponseType(typeof(RunnerJobManifest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Lease([FromBody] RunnerLeaseHttpRequest request, CancellationToken ct)
    {
        var runnerId = RunnerCallerIdentity.RunnerId(this.HttpContext);
        if (runnerId is null)
        {
            return this.Unauthorized(new RunnerContractError(RunnerContractError.RegistrationRevoked, "No runner credential was resolved."));
        }

        var offer = await offers.OfferAsync(
            new RunnerLeaseRequest(runnerId.Value, request.FreeSlots, request.ContractVersion),
            ct);

        if (offer.Granted)
        {
            // Stamped here because this is replica identity, and identity is what the controller adds.
            // The mirror the manifest points at is this replica's disk and the per-lease registries are
            // this replica's process, so on a multi-replica installation the runner has to call this
            // replica by name. The load-balanced URL it leased through reaches whichever replica is next.
            var advertised = leaseOptions.Value.AdvertisedRunnerUrl;
            return this.Ok(
                string.IsNullOrWhiteSpace(advertised)
                    ? offer.Manifest
                    : offer.Manifest! with { ServedBy = advertised });
        }

        // Counted here rather than deeper down, because this is the one place every refusal passes through
        // on its way to a runner, and a refusal an operator never sees is an idle queue with no explanation.
        if (offer.Refusal is RunnerLeaseRefusal.SlotLimitReached or RunnerLeaseRefusal.NotLicensed)
        {
            metrics?.RecordSlotRefusal(offer.Refusal);
        }

        // An empty queue is not an error, and answering it with one would have every idle runner logging
        // failures. Everything an operator has to act on is a refusal with its own status.
        return offer.Refusal switch
        {
            RunnerLeaseRefusal.NoMatchingWork or RunnerLeaseRefusal.NoFreeCapacity => this.NoContent(),
            RunnerLeaseRefusal.UnsupportedContractVersion => this.StatusCode(
                StatusCodes.Status409Conflict,
                new RunnerContractError(RunnerContractError.UnsupportedContractVersion, offer.Detail ?? "Unsupported contract version.")),
            RunnerLeaseRefusal.RegistrationNotUsable => this.Unauthorized(
                new RunnerContractError(RunnerContractError.RegistrationRevoked, "This registration can no longer lease.")),
            RunnerLeaseRefusal.SlotLimitReached or RunnerLeaseRefusal.NotLicensed => this.StatusCode(
                StatusCodes.Status429TooManyRequests,
                new RunnerContractError(RunnerContractError.SlotLimitReached, offer.Detail ?? "No entitled runner slot is free.")),
            RunnerLeaseRefusal.Draining => this.StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new RunnerContractError("draining", offer.Detail ?? "This control plane is draining and is issuing no new leases.")),
            _ => this.NoContent(),
        };
    }

    /// <summary>
    ///     Keeps a lease alive while the review runs, and carries back the one instruction the control
    ///     plane has for a job already in flight.
    ///     <para>
    ///         Answered 200 whether or not the renewal was accepted. A refusal has a reason the executor
    ///         must act on differently. A lost lease means stop without reporting, and a revoked client
    ///         means stop and report the reason. A status code alone cannot carry that distinction.
    ///     </para>
    /// </summary>
    /// <param name="request">The job and generation being renewed.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("lease/heartbeat")]
    [ProducesResponseType(typeof(RunnerHeartbeatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Heartbeat([FromBody] RunnerLeaseHeartbeatRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var runnerId = RunnerCallerIdentity.RunnerId(this.HttpContext);
        if (runnerId is null)
        {
            return this.Unauthorized(new RunnerContractError(RunnerContractError.RegistrationRevoked, "No runner credential was resolved."));
        }

        // The heartbeat was the one runner operation with no version at all, which meant a control-plane
        // deploy could leave a mid-flight job renewing a lease happily while every execution call it made
        // was refused for version skew. Validated only when reported: an older runner sends nothing and is
        // gated at its next lease instead.
        if (request.ContractVersion is int reported && !RunnerContractVersion.IsSupported(reported))
        {
            return this.Conflict(RunnerContractError.ForUnsupportedVersion(reported));
        }

        // The same renewal an in-process execution performs, against the same store. A separate rule here
        // would let a remote job outlive a lease an in-process one would have lost.
        var renewal = await leases.TryRenewAsync(
            new ReviewJobLease(request.JobId, runnerId.Value.ToString("D"), request.LeaseGeneration, DateTimeOffset.UtcNow),
            TimeSpan.FromSeconds(leaseOptions.Value.LeaseDurationSeconds),
            ct);

        return this.Ok(
            new RunnerHeartbeatResponse
            {
                Accepted = renewal.Accepted,
                ExpiresAt = renewal.ExpiresAt,
                StopReason = renewal.StopReason.ToString(),
            });
    }

    /// <summary>Hands a lease back deliberately, so a planned shutdown costs the job nothing.</summary>
    /// <param name="request">The job and generation being handed back.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("lease/release")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(RunnerContractError), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Release([FromBody] RunnerLeaseHandbackRequest request, CancellationToken ct)
    {
        var runnerId = RunnerCallerIdentity.RunnerId(this.HttpContext);
        if (runnerId is null)
        {
            return this.Unauthorized(new RunnerContractError(RunnerContractError.RegistrationRevoked, "No runner credential was resolved."));
        }

        var owner = runnerId.Value.ToString("D");
        var call = new RunnerCallContext(request.JobId, request.LeaseGeneration, owner);

        // Authorized like every other proxied operation rather than trusting the body. A release is a write
        // that returns another runner's job to the queue, so it is a call worth authenticating strictly.
        var authorization = await authorizer.AuthorizeAsync(call, ct);
        if (!authorization.IsAuthorized)
        {
            return this.Conflict(new RunnerContractError(RunnerContractError.LeaseNotHeld, authorization.Refusal.ToString()));
        }

        // A failure spends one of the job's reclaim attempts; a drain costs it nothing. A release that
        // does not say which is treated as a drain, which is what every release was before the reason
        // existed, so an older runner keeps the behaviour it had.
        var lease = new ReviewJobLease(request.JobId, owner, request.LeaseGeneration, DateTimeOffset.UtcNow);
        var released = string.Equals(request.Reason, RunnerLeaseReleaseReasons.Failure, StringComparison.Ordinal)
            ? await leases.TryReleaseFailedAsync(
                lease,
                leaseOptions.Value.MaxConsecutiveReclaims,
                leaseOptions.Value.MaxTotalReclaims,
                ct) != ReviewJobReclaimOutcome.NotReclaimed
            : await leases.TryReleaseAsync(lease, ct);

        // Whether or not the lease was still held, this runner is done with the job, and the scope it was
        // charging against is this replica's to drop, along with the served completions, the submission
        // memory, and the workspace's disk. Every later call for this job is refused by lease
        // authorization, so nothing can legitimately need them again.
        budgets.Release(request.JobId);
        replays.Release(request.JobId);
        submissions.Release(request.JobId);
        await workspaces.ReleaseAsync(request.JobId);

        return released
            ? this.Ok(new { released = true })
            : this.Conflict(new RunnerContractError(RunnerContractError.LeaseNotHeld, "The lease was no longer held."));
    }
}

/// <summary>A runner asking for work.</summary>
public sealed class RunnerLeaseHttpRequest
{
    /// <summary>How many more jobs this runner can take right now.</summary>
    public int FreeSlots { get; init; }

    /// <summary>The contract version the runner speaks.</summary>
    public int ContractVersion { get; init; }
}

/// <summary>A runner handing a lease back.</summary>
public sealed class RunnerLeaseHandbackRequest
{
    /// <summary>The job whose lease is being returned.</summary>
    public Guid JobId { get; init; }

    /// <summary>The generation the runner believes it holds.</summary>
    public int LeaseGeneration { get; init; }

    /// <summary>
    ///     Why the lease is coming back. See <see cref="RunnerLeaseReleaseReasons" />. Absent from an older
    ///     runner, which reads as a drain: the uncounted release every handback was before the field existed.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>A runner saying it is still working on a job.</summary>
public sealed class RunnerLeaseHeartbeatRequest
{
    /// <summary>The job whose lease is being renewed.</summary>
    public Guid JobId { get; init; }

    /// <summary>The generation the runner believes it holds.</summary>
    public int LeaseGeneration { get; init; }

    /// <summary>
    ///     The contract version the runner speaks, absent from runners older than the field. Carried so a
    ///     control-plane deploy mid-review surfaces as a refused renewal naming the skew, not as a healthy
    ///     lease over a job whose every other call is refused.
    /// </summary>
    public int? ContractVersion { get; init; }
}

/// <summary>What a heartbeat learns: whether the lease is still held, until when, and why not.</summary>
public sealed class RunnerHeartbeatResponse
{
    /// <summary>Whether the runner still holds the lease it named.</summary>
    public bool Accepted { get; init; }

    /// <summary>When the renewed lease expires, or null when it was not renewed.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    ///     Why the renewal was refused, as a stable token. <c>None</c> on an accepted renewal. The executor
    ///     needs this rather than a status code. A lost lease means the job belongs to another runner and
    ///     this one stops without reporting, while a revoked client or an exhausted budget is an operator
    ///     decision that has to
    ///     be reported as one.
    /// </summary>
    public string StopReason { get; init; } = string.Empty;
}
