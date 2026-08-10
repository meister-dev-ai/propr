// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     What an operator can see and do about the runner fleet.
///     <para>
///         Gated by the same capability that gates distributed execution, so an installation that cannot
///         run reviews on runners does not get a page implying it can. Refused with the capability's own
///         message rather than a bare 403, which is the difference between "you may not" and "buy this".
///     </para>
/// </summary>
[ApiController]
[Route("admin/runners")]
public sealed class AdminRunnersController(
    IRunnerRegistrationService? runners = null,
    IRunnerRegistry? registry = null,
    IRunnerFleetMonitor? fleet = null,
    IRunnerWorkloadReader? workload = null,
    ILicensingCapabilityService? licensing = null,
    IOptions<RunnerFleetOptions>? fleetOptions = null,
    IClientAdminService? clients = null,
    ITenantAdminService? tenants = null) : ControllerBase
{
    /// <summary>
    ///     Upper bound on the hours that can be added to the current time without leaving the range of
    ///     <see cref="DateTimeOffset" />. This is not a policy limit. An operator may ask for any lifetime,
    ///     and a request beyond this range is stored as a token that does not expire.
    /// </summary>
    private const int MaxRepresentableHours = 24 * 365 * 1000;

    /// <summary>
    ///     The whole installation's fleet, every tenant together, for the operator who administers all of
    ///     them. Each runner names the tenant it belongs to, because across tenants a display name alone
    ///     does not identify a host.
    /// </summary>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpGet]
    [ProducesResponseType(typeof(RunnerRegistryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ListAll(CancellationToken ct = default)
    {
        var refusal = await this.RequirePlatformOperatorAsync(ct);
        if (refusal is not null)
        {
            return refusal;
        }

        var all = tenants is null ? [] : await tenants.GetAllAsync(ct);

        return this.Ok(
            await this.BuildRegistryAsync(
                [.. all.Select(tenant => (tenant.Id, (string?)tenant.DisplayName))],
                includeInstallationStall: true,
                ct));
    }

    /// <summary>Every runner enrolled in one tenant, with its health, scope, and current work.</summary>
    /// <param name="tenantId">The tenant whose registry to read.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpGet("{tenantId:guid}")]
    [ProducesResponseType(typeof(RunnerRegistryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> List(Guid tenantId, CancellationToken ct = default)
    {
        var refusal = await this.RequireOperatorAsync(ct) ?? this.RequireTenantAccess(tenantId);
        if (refusal is not null)
        {
            return refusal;
        }

        var tenant = tenants is null ? null : await tenants.GetByIdAsync(tenantId, ct);

        // The installation-wide stall is not included here. This view is readable by the tenant's own
        // administrators, and the stall counts work queued across every tenant. The tenant's own pending
        // count is still reported.
        return this.Ok(
            await this.BuildRegistryAsync(
                [(tenantId, tenant?.DisplayName)],
                includeInstallationStall: false,
                ct));
    }

    /// <summary>
    ///     Assembles the registry for one tenant or for all of them. Reading tenant by tenant keeps one
    ///     assembly routine behind both views, so the two views report the same figures.
    /// </summary>
    private async Task<RunnerRegistryDto> BuildRegistryAsync(
        IReadOnlyList<(Guid Id, string? Name)> scope,
        bool includeInstallationStall,
        CancellationToken ct)
    {
        var status = fleet is null ? null : await fleet.GetStatusAsync(ct);

        // Health is computed here rather than in the browser. The server already applies the configured
        // liveness window and the contract-compatibility rule; a second implementation in the client would
        // drift from it and show Active for a runner the server counts as unusable capacity.
        var activeWindow = fleetOptions?.Value.ActiveHeartbeatWindow ?? TimeSpan.FromSeconds(120);
        var now = DateTimeOffset.UtcNow;

        // A day, because that is the window an operator reads a fleet in: "did this host do anything since
        // yesterday" separates a runner that is merely idle right now from one that has never worked.
        var completedSince = now.AddDays(-1);

        var runnerDtos = new List<RunnerDto>();
        var tokenDtos = new List<RunnerRegistrationTokenSummaryDto>();
        var executingJobs = 0;
        var pendingJobs = 0;
        DateTimeOffset? oldestPending = null;

        foreach (var (tenantId, tenantName) in scope)
        {
            var enrolled = await registry!.ListAsync(tenantId, ct);
            var tokens = await registry!.ListTokensAsync(tenantId, ct);
            var fleetWorkload = workload is null
                ? RunnerFleetWorkload.Empty
                : await workload.GetWorkloadAsync(tenantId, completedSince, ct);

            runnerDtos.AddRange(
                enrolled.Select(runner => ToDto(
                    runner,
                    now,
                    activeWindow,
                    fleetWorkload.ByRunner.GetValueOrDefault(runner.Id),
                    tenantName)));

            tokenDtos.AddRange(
                tokens.Select(token => new RunnerRegistrationTokenSummaryDto(
                    token.Id,
                    token.IssuedAt,
                    token.ExpiresAt,
                    token.MaxUses - token.UseCount)));

            executingJobs += fleetWorkload.ExecutingJobCount;
            pendingJobs += fleetWorkload.PendingJobCount;

            if (fleetWorkload.OldestPendingSince is { } oldest
                && (oldestPending is null || oldest < oldestPending))
            {
                oldestPending = oldest;
            }
        }

        // Counted from the runners actually in view rather than taken from the installation-wide monitor.
        // On the tenant view the monitor's figure would count other tenants' hosts, which reads as capacity
        // the tenant does not have and is not theirs to know about.
        var activeRunners = runnerDtos.Count(runner => runner.Health == "active");

        return new RunnerRegistryDto(
            runnerDtos,
            activeRunners,
            status?.Mode.ToString() ?? "InProcess",
            executingJobs,
            pendingJobs,
            oldestPending,
            tokenDtos,
            !includeInstallationStall || status?.Stall is null
                ? null
                : new QueueStallDto(
                    status.Stall.Cause.ToString(),
                    status.Stall.PendingJobCount,
                    status.Stall.OldestPendingSince,
                    status.Stall.Detail));
    }

    /// <summary>
    ///     Issues a registration token. The value is returned here and never again. Single-use unless the
    ///     request asks for more, which is what a scaling group needs: its replicas start without an
    ///     operator present to issue each of them one.
    /// </summary>
    /// <param name="request">Which tenant and clients the enrolled runner will be scoped to.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("tokens")]
    [ProducesResponseType(typeof(RunnerRegistrationTokenDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> IssueToken([FromBody] IssueRunnerTokenRequest request, CancellationToken ct = default)
    {
        var refusal = await this.RequireOperatorAsync(ct)
                      ?? this.RequireTenantAccess(request.TenantId)
                      ?? await this.RefuseForeignClientScopeAsync(request.TenantId, request.ClientScope ?? [], ct);
        if (refusal is not null)
        {
            return refusal;
        }

        // How long a token lives and how many hosts it enrolls are the operator's calls, and there is no
        // ceiling they have to argue with: an installation that wants a key its scaling group can use
        // indefinitely is describing a real deployment rather than a mistake. Absent means unbounded on
        // both. A value that is present and at or below zero is refused, because it would create a
        // token that cannot be used.
        if (request.ValidForHours is <= 0)
        {
            return this.BadRequest(
                new
                {
                    error = "invalid_token_lifetime",
                    message = "validForHours must be a positive number of hours, or omitted for a token that does not expire.",
                });
        }

        if (request.MaxUses is <= 0)
        {
            return this.BadRequest(
                new
                {
                    error = "invalid_token_uses",
                    message = "maxUses must be at least 1, or omitted for a token with no enrollment limit.",
                });
        }

        var issue = await runners!.IssueRegistrationTokenAsync(
            request.TenantId,
            request.ClientScope ?? [],
            // Hours become a span here rather than in the service, which has no reason to know the unit an
            // operator typed. Clamped to the calendar so an absurd number reads as "does not expire"
            // instead of overflowing.
            request.ValidForHours is { } hours ? TimeSpan.FromHours(Math.Min(hours, MaxRepresentableHours)) : null,
            AuthHelpers.GetUserId(this.HttpContext) ?? Guid.Empty,
            request.MaxUses,
            ct);

        return this.StatusCode(
            StatusCodes.Status201Created,
            new RunnerRegistrationTokenDto(issue.TokenId, issue.Token, issue.ExpiresAt));
    }

    /// <summary>Revokes a runner. It stops being able to lease immediately.</summary>
    /// <param name="runnerId">The runner to revoke.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("{runnerId:guid}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid runnerId, CancellationToken ct = default)
    {
        var refusal = await this.RequireOperatorAsync(ct)
                      ?? await this.RequireRunnerAccessAsync(runnerId, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runners!.RevokeAsync(runnerId, ct) ? this.NoContent() : this.NotFound();
    }

    /// <summary>
    ///     Deletes a runner's row from the registry. A host that was redeployed and re-enrolled has a
    ///     new row, and the old one stops counting as capacity once it is deleted. The delete is refused
    ///     while the runner holds a lease. Revoke it first, wait for the lease to expire, then delete.
    /// </summary>
    /// <param name="runnerId">The runner to delete.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpDelete("{runnerId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid runnerId, CancellationToken ct = default)
    {
        var refusal = await this.RequireOperatorAsync(ct)
                      ?? await this.RequireRunnerAccessAsync(runnerId, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runners!.DeleteAsync(runnerId, ct) switch
        {
            RunnerDeletionOutcome.Deleted => this.NoContent(),
            RunnerDeletionOutcome.HoldingLease => this.Conflict(
                new
                {
                    error = "runner_holds_lease",
                    message = "This runner still holds a review's lease. Revoke it so it stops renewing, "
                              + "wait for the lease to expire, then delete it.",
                }),
            _ => this.NotFound(),
        };
    }

    /// <summary>Revokes an issued registration token so it can no longer enroll anything.</summary>
    /// <param name="tokenId">The token to revoke.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPost("tokens/{tokenId:guid}/revoke")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeToken(Guid tokenId, CancellationToken ct = default)
    {
        var refusal = await this.RequireOperatorAsync(ct)
                      ?? await this.RequireTokenAccessAsync(tokenId, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runners!.RevokeRegistrationTokenAsync(tokenId, ct) ? this.NoContent() : this.NotFound();
    }

    /// <summary>Re-stamps which clients a runner may serve, taking effect on its next lease.</summary>
    /// <param name="runnerId">The runner.</param>
    /// <param name="request">The new scope.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    [HttpPut("{runnerId:guid}/scope")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AssignScope(
        Guid runnerId,
        [FromBody] AssignRunnerScopeRequest request,
        CancellationToken ct = default)
    {
        var refusal = await this.RequireOperatorAsync(ct);
        if (refusal is not null)
        {
            return refusal;
        }

        var runner = await registry!.FindByIdAsync(runnerId, ct);
        if (runner is null)
        {
            return this.NotFound();
        }

        var scope = request.ClientScope ?? [];
        refusal = this.RequireTenantAccess(runner.TenantId)
                  ?? await this.RefuseForeignClientScopeAsync(runner.TenantId, scope, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        return await runners!.AssignClientScopeAsync(runnerId, scope, ct)
            ? this.NoContent()
            : this.NotFound();
    }

    private static RunnerDto ToDto(
        Domain.Entities.ReviewRunner runner,
        DateTimeOffset now,
        TimeSpan activeWindow,
        RunnerWorkload? workload,
        string? tenantName)
    {
        return new RunnerDto(
            runner.Id,
            runner.DisplayName,
            runner.State.ToString(),
            [.. runner.ClientScope],
            [.. runner.Tags],
            runner.ContractVersion,
            runner.LastSeenAt,
            runner.CredentialExpiresAt,
            runner.EnrolledAt,
            ResolveHealth(runner, now, activeWindow),
            workload?.ExecutingCount ?? 0,
            workload?.CompletedCount ?? 0,
            [
                .. (workload?.Executing ?? []).Select(job => new RunnerJobDto(
                    job.JobId,
                    job.RepositoryName,
                    job.PullRequestNumber,
                    job.Title,
                    job.StartedAt,
                    job.ReclaimCount)),
            ],
            runner.TenantId,
            tenantName);
    }

    /// <summary>
    ///     The same four clauses the fleet monitor applies, so the registry and the execution decision
    ///     cannot disagree about whether a runner is alive. A runner refused for its contract version is
    ///     called out by name: it is the rolling-upgrade case, and "not responding" would send an operator
    ///     looking at the network instead of at the version.
    /// </summary>
    private static string ResolveHealth(Domain.Entities.ReviewRunner runner, DateTimeOffset now, TimeSpan activeWindow)
    {
        if (runner.State != RunnerState.Enrolled)
        {
            return "revoked";
        }

        if (!RunnerContractVersion.IsSupported(runner.ContractVersion))
        {
            return "incompatible";
        }

        return runner.LastSeenAt is not null && now - runner.LastSeenAt.Value <= activeWindow
            ? "active"
            : "stale";
    }

    /// <summary>
    ///     Operator, then capability, in that order. Answering "not licensed" before checking the caller
    ///     would disclose the installation's licensing to an unauthenticated caller.
    ///     <para>
    ///         An operator here is a platform administrator or an administrator of any tenant. It only
    ///         establishes that the caller administers <em>something</em>; which tenant's registry they may
    ///         touch is decided separately by <see cref="RequireTenantAccess" />, against the tenant that
    ///         owns the thing being acted on.
    ///     </para>
    /// </summary>
    private async Task<IActionResult?> RequireOperatorAsync(CancellationToken ct)
    {
        if (!AuthHelpers.IsAdmin(this.HttpContext))
        {
            var auth = AuthHelpers.RequireAnyTenantRole(this.HttpContext, TenantRole.TenantAdministrator);
            if (auth is not null)
            {
                return auth;
            }
        }

        return await this.RequireCapabilityAsync(ct);
    }

    /// <summary>
    ///     Platform administrators only. The installation-wide view spans tenants, so no tenant role can
    ///     authorize it: administering one tenant is not grounds for reading the rest.
    /// </summary>
    private async Task<IActionResult?> RequirePlatformOperatorAsync(CancellationToken ct)
    {
        var auth = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        return auth ?? await this.RequireCapabilityAsync(ct);
    }

    /// <summary>
    ///     Whether the caller may act on <paramref name="tenantId" />: platform administrators may act on
    ///     every tenant, and a tenant's own administrators on theirs.
    ///     <para>
    ///         Callers must pass the tenant that owns the runner or token, read from the stored entity
    ///         and not from the request. A tenant supplied by the request would authorize the caller
    ///         against a tenant they chose, while the operation ran against the real owner.
    ///     </para>
    /// </summary>
    private IActionResult? RequireTenantAccess(Guid tenantId)
    {
        if (AuthHelpers.IsAdmin(this.HttpContext))
        {
            return null;
        }

        // A runner enrolled in the System tenant is offered every tenant's work, so enrolling one hands a
        // host the right to fetch any customer's source. That is a platform decision however the System
        // tenant's own memberships happen to be administered, so no tenant role reaches it.
        return TenantCatalog.IsSystemTenant(tenantId)
            ? AuthHelpers.RequirePlatformAdmin(this.HttpContext)
            : AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
    }

    /// <summary>
    ///     Resolves the runner the id refers to and authorizes the caller against the tenant that owns
    ///     it. Endpoints keyed only by a runner id carry no tenant, so the owner is read before the caller
    ///     is authorized. Otherwise the id alone would determine which runner is affected.
    /// </summary>
    private async Task<IActionResult?> RequireRunnerAccessAsync(Guid runnerId, CancellationToken ct)
    {
        var runner = await registry!.FindByIdAsync(runnerId, ct);
        return runner is null ? this.NotFound() : this.RequireTenantAccess(runner.TenantId);
    }

    /// <summary>The same rule as <see cref="RequireRunnerAccessAsync" />, for an issued enrollment token.</summary>
    private async Task<IActionResult?> RequireTokenAccessAsync(Guid tokenId, CancellationToken ct)
    {
        var token = await registry!.FindTokenByIdAsync(tokenId, ct);
        return token is null ? this.NotFound() : this.RequireTenantAccess(token.TenantId);
    }

    /// <summary>
    ///     Refuses a client scope naming clients the tenant does not own.
    ///     <para>
    ///         Such an entry has no effect. The lease offer matches a runner to work by joining the
    ///         runner's tenant to the client's tenant before it reads the stamped scope, so a client of
    ///         another tenant is never offered. It is refused rather than stored, because a stored entry
    ///         reads back as a configured scope while the runner receives no work for it.
    ///     </para>
    /// </summary>
    private async Task<IActionResult?> RefuseForeignClientScopeAsync(
        Guid tenantId,
        IReadOnlyList<Guid> clientScope,
        CancellationToken ct)
    {
        // A System-tenant runner serves every tenant by design, so every client is legitimately within its
        // reach and there is no foreign one to refuse. Applying the tenant rule here would make a shared
        // runner the one kind that cannot be narrowed to particular clients.
        if (clientScope.Count == 0 || clients is null || TenantCatalog.IsSystemTenant(tenantId))
        {
            return null;
        }

        var resolved = await clients.GetByIdsAsync(clientScope, ct);
        var foreign = clientScope
            .Except(resolved.Where(client => client.TenantId == tenantId).Select(client => client.Id))
            .ToArray();

        return foreign.Length == 0
            ? null
            : this.BadRequest(
                new
                {
                    error = "client_outside_tenant",
                    message = "A runner may only be scoped to clients of the tenant it enrolled in. "
                              + "These are not that tenant's, so a runner scoped to them would be offered "
                              + "no work at all: "
                              + string.Join(", ", foreign),
                });
    }

    private async Task<IActionResult?> RequireCapabilityAsync(CancellationToken ct)
    {
        if (runners is null || registry is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensing,
            PremiumCapabilityKey.DistributedExecution,
            ct);

        return capability is null ? null : new PremiumFeatureUnavailableResult(capability);
    }
}

/// <summary>The runner registry as an operator sees it.</summary>
/// <param name="Runners">Every runner in the tenant, whatever its state.</param>
/// <param name="ActiveRunnerCount">How many currently count as capacity.</param>
/// <param name="ExecutionMode">Whether reviews are running on runners or in the control plane.</param>
/// <param name="ExecutingJobCount">Reviews the fleet is running right now.</param>
/// <param name="PendingJobCount">
///     Reviews waiting for one of this tenant's runners. Reported whether or not the queue counts as
///     stalled: a queue that is merely deep and a queue that nothing is taking look the same to an
///     operator who can only see the second one.
/// </param>
/// <param name="OldestPendingSince">When the longest-waiting review was submitted, or null when none wait.</param>
/// <param name="PendingTokens">Issued enrollment tokens that can still be used.</param>
/// <param name="Stall">The queue-stall condition, when there is one.</param>
public sealed record RunnerRegistryDto(
    IReadOnlyList<RunnerDto> Runners,
    int ActiveRunnerCount,
    string ExecutionMode,
    int ExecutingJobCount,
    int PendingJobCount,
    DateTimeOffset? OldestPendingSince,
    IReadOnlyList<RunnerRegistrationTokenSummaryDto> PendingTokens,
    QueueStallDto? Stall);

/// <summary>
///     An issued enrollment token that has not been used up, expired, or revoked. The secret is not here
///     and cannot be: only its hashes were stored.
/// </summary>
/// <param name="TokenId">Identity, for revoking it.</param>
/// <param name="IssuedAt">When it was issued.</param>
/// <param name="ExpiresAt">When it stops being usable.</param>
/// <param name="RemainingUses">How many enrollments it can still perform.</param>
public sealed record RunnerRegistrationTokenSummaryDto(
    Guid TokenId,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    int? RemainingUses);

/// <summary>One enrolled runner.</summary>
/// <param name="Id">Identity.</param>
/// <param name="DisplayName">Operator-facing name the runner declared.</param>
/// <param name="State">Enrolled or revoked.</param>
/// <param name="ClientScope">The clients it may serve. Empty means every client in the tenant.</param>
/// <param name="Tags">Tags it declares.</param>
/// <param name="ContractVersion">The contract version it reported.</param>
/// <param name="LastSeenAt">When it last authenticated, or null if never.</param>
/// <param name="CredentialExpiresAt">When its credential must be renewed by.</param>
/// <param name="EnrolledAt">When it enrolled.</param>
/// <param name="Health">
///     What the server makes of it: active, stale, incompatible, or revoked. Computed here so the
///     registry cannot disagree with the execution decision about whether a runner is usable.
/// </param>
/// <param name="ExecutingJobCount">Reviews it holds a lease on right now.</param>
/// <param name="CompletedJobCount">Reviews it finished in the last day.</param>
/// <param name="Executing">
///     The reviews it is running, up to a bound. Named rather than counted because the count alone cannot
///     tell an operator whether a runner is working or stuck on one thing.
/// </param>
public sealed record RunnerDto(
    Guid Id,
    string DisplayName,
    string State,
    IReadOnlyList<Guid> ClientScope,
    IReadOnlyList<string> Tags,
    int ContractVersion,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CredentialExpiresAt,
    DateTimeOffset EnrolledAt,
    string Health,
    int ExecutingJobCount,
    int CompletedJobCount,
    IReadOnlyList<RunnerJobDto> Executing,
    Guid TenantId,
    string? TenantName);

/// <summary>One review a runner is holding.</summary>
/// <param name="JobId">The review job.</param>
/// <param name="RepositoryName">The repository, as the provider names it.</param>
/// <param name="PullRequestNumber">The pull request under review.</param>
/// <param name="Title">The pull request's title, when the job recorded one.</param>
/// <param name="StartedAt">When the runner started it.</param>
/// <param name="ReclaimCount">How many times this review has been taken back from a runner that went quiet.</param>
public sealed record RunnerJobDto(
    Guid JobId,
    string? RepositoryName,
    int PullRequestNumber,
    string? Title,
    DateTimeOffset? StartedAt,
    int ReclaimCount);

/// <summary>A queue that has work nothing is taking.</summary>
/// <param name="Cause">Why.</param>
/// <param name="PendingJobCount">How many jobs are waiting.</param>
/// <param name="OldestPendingSince">When the longest-waiting job was submitted.</param>
/// <param name="Detail">Operator-readable detail.</param>
public sealed record QueueStallDto(
    string Cause,
    int PendingJobCount,
    DateTimeOffset OldestPendingSince,
    string? Detail);

/// <summary>A request to mint a registration token.</summary>
public sealed class IssueRunnerTokenRequest
{
    /// <summary>The tenant the enrolled runner will belong to.</summary>
    public Guid TenantId { get; init; }

    /// <summary>The clients it may serve. Empty or absent means every client in the tenant.</summary>
    public IReadOnlyList<Guid>? ClientScope { get; init; }

    /// <summary>
    ///     How long the token stays usable, in hours. Omit it for a token that does not expire, which is
    ///     the usual choice for a key a scaling group reads from its secret store.
    /// </summary>
    public int? ValidForHours { get; init; }

    /// <summary>
    ///     How many hosts may enroll with this token. Omit it for no limit. One suits a host enrolled by
    ///     hand; a scaling group needs more, because its replicas start without an operator present to
    ///     issue each of them a token.
    /// </summary>
    public int? MaxUses { get; init; }
}

/// <summary>A request to re-scope a runner.</summary>
public sealed class AssignRunnerScopeRequest
{
    /// <summary>The new scope. Empty or absent means every client in the tenant.</summary>
    public IReadOnlyList<Guid>? ClientScope { get; init; }
}

/// <summary>A freshly issued token. The value appears here and is never retrievable again.</summary>
/// <param name="TokenId">Identity of the token.</param>
/// <param name="Token">The secret, shown once.</param>
/// <param name="ExpiresAt">When it stops being usable.</param>
public sealed record RunnerRegistrationTokenDto(Guid TokenId, string Token, DateTimeOffset? ExpiresAt);
