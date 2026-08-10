// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Security.Cryptography;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Security;
using MeisterDev.ProPR.Domain;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     Enrolls runners, renews their credentials, and revokes them.
///     <para>
///         The one rule everything else rests on: the client scope comes from the operator-issued token, and
///         nothing in a registration payload can name it. Scope is then structural rather than procedural,
///         and a mis-configured runner is a routing mistake instead of a way to read another client's code.
///     </para>
/// </summary>
public sealed partial class RunnerRegistrationService(
    IRunnerRegistry registry,
    IPasswordHashService hashes,
    TimeProvider timeProvider,
    ILogger<RunnerRegistrationService> logger,
    ILicensingCapabilityService? licensing = null) : IRunnerRegistrationService
{
    /// <summary>How long an issued runner credential is valid before it must be renewed.</summary>
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromDays(30);

    /// <summary>
    ///     The window for a runner enrolled in the System tenant. Such a host is offered every tenant's
    ///     work, so its credential opens every tenant's source rather than one customer's, and a stolen one
    ///     should stop working sooner than a stolen tenant-scoped one. A running host pays nothing for the
    ///     shorter window, because renewal starts an hour before expiry and keeps the same identity and
    ///     scope. Only a host that was off for longer than this window enrolls again.
    /// </summary>
    private static readonly TimeSpan SharedRunnerCredentialLifetime = TimeSpan.FromDays(7);

    /// <summary>How long a credential issued to this tenant's runners lasts.</summary>
    /// <param name="tenantId">The tenant the runner belongs to.</param>
    private static TimeSpan LifetimeFor(Guid tenantId)
    {
        return SystemTenant.Is(tenantId) ? SharedRunnerCredentialLifetime : CredentialLifetime;
    }

    /// <summary>
    ///     How stale the recorded last-seen time may get before authenticating a call writes a fresh one. A
    ///     busy runner authenticates once per proxied call, several per file in flight, so recording every
    ///     one of them would turn a liveness field into a write on the hot path for no extra information.
    /// </summary>
    private static readonly TimeSpan LastSeenResolution = TimeSpan.FromMinutes(1);

    /// <inheritdoc />
    public async Task<RunnerRegistrationResult> RegisterAsync(
        RunnerRegistrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!RunnerContractVersion.IsSupported(request.ContractVersion))
        {
            return RunnerRegistrationResult.Refused(RunnerContractVersion.DescribeMismatch(request.ContractVersion));
        }

        // Refused before the token is even looked at. An installation that cannot run distributed reviews
        // should not accumulate enrollments that can never lease, and burning a single-use token to learn
        // that would leave an operator with nothing to retry.
        if (licensing is not null
            && !await licensing.IsEnabledAsync(PremiumCapabilityKey.DistributedExecution, ct))
        {
            LogRegistrationRefused(logger);
            return RunnerRegistrationResult.Refused("Distributed review execution is not licensed for this installation.");
        }

        var now = timeProvider.GetUtcNow();
        var token = await registry.FindTokenAsync(PatTokenLookupHash.Compute(request.RegistrationToken), ct);

        // One refusal for every way a token can be unusable. Telling a caller which way it failed tells an
        // attacker whether the token ever existed.
        if (token is null
            || !token.IsUsableAt(now)
            || !hashes.Verify(request.RegistrationToken, token.TokenHash))
        {
            LogRegistrationRefused(logger);
            return RunnerRegistrationResult.Refused("The registration token is not valid.");
        }

        var (secret, credentialHash, lookupHash) = this.IssueCredential();
        var runner = new ReviewRunner(
            Guid.NewGuid(),
            token.TenantId,
            request.DisplayName,
            // From the token, never from the request. The request has nowhere to put a scope.
            token.ClientScope,
            request.ContractVersion,
            credentialHash,
            lookupHash,
            now + LifetimeFor(token.TenantId),
            now);
        runner.DeclareTags(request.Tags);

        token.RecordUse();
        await registry.AddAsync(runner, token, ct);

        LogRunnerEnrolled(logger, runner.Id, runner.TenantId, runner.ClientScope.Count);
        return RunnerRegistrationResult.Enrolled(runner.Id, secret, runner.CredentialExpiresAt);
    }

    /// <inheritdoc />
    public async Task<RunnerRegistrationResult> RenewCredentialAsync(
        Guid runnerId,
        string currentCredential,
        int contractVersion,
        CancellationToken ct = default)
    {
        var runner = await registry.FindByIdAsync(runnerId, ct);
        if (runner is null
            || runner.State != RunnerState.Enrolled
            || !hashes.Verify(currentCredential, runner.CredentialHash))
        {
            LogRenewalRefused(logger, runnerId);
            return RunnerRegistrationResult.Refused("The runner credential is not valid.");
        }

        if (!RunnerContractVersion.IsSupported(contractVersion))
        {
            return RunnerRegistrationResult.Refused(RunnerContractVersion.DescribeMismatch(contractVersion));
        }

        var (secret, credentialHash, lookupHash) = this.IssueCredential();

        // Same identity, same scope. Renewal exists so a credential can expire without an operator having
        // to enroll the host again, and re-stamping the scope here would quietly undo an operator's change.
        runner.RenewCredential(
            credentialHash,
            lookupHash,
            timeProvider.GetUtcNow() + LifetimeFor(runner.TenantId),
            contractVersion);
        await registry.UpdateAsync(runner, ct);

        return RunnerRegistrationResult.Enrolled(runner.Id, secret, runner.CredentialExpiresAt);
    }

    /// <inheritdoc />
    public async Task<ReviewRunner?> AuthenticateAsync(string credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var runner = await registry.FindByCredentialLookupAsync(PatTokenLookupHash.Compute(credential), ct);
        if (runner is null
            || runner.State != RunnerState.Enrolled
            || runner.CredentialExpiresAt <= now
            || !hashes.Verify(credential, runner.CredentialHash))
        {
            return null;
        }

        // Recorded here rather than by the caller, because a caller that only mutates the entity leaves
        // last-seen in memory and the field never reaches the database at all.
        if (runner.LastSeenAt is null || now - runner.LastSeenAt >= LastSeenResolution)
        {
            runner.MarkSeen(now);
            await registry.UpdateAsync(runner, ct);
        }

        return runner;
    }

    /// <inheritdoc />
    public async Task<RunnerRegistrationTokenIssue> IssueRegistrationTokenAsync(
        Guid tenantId,
        IReadOnlyList<Guid> clientScope,
        TimeSpan? validFor,
        Guid issuedByUserId,
        int? maxUses = 1,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientScope);

        if (validFor is { } requested)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requested, TimeSpan.Zero);
        }

        if (maxUses is { } uses)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(uses, 1);
        }

        var now = timeProvider.GetUtcNow();

        // Checked here rather than trusted from the caller. A lifetime that overflows the addition would
        // otherwise be stored as a wrapped expiry, and one of zero or less mints a real secret into a
        // token that is unusable the moment it is handed over. A token asked to outlive the calendar is
        // asking for one that does not expire, so it is given one rather than refused.
        var expiresAt = validFor is { } lifetime && lifetime <= DateTimeOffset.MaxValue - now
            ? now + lifetime
            : (DateTimeOffset?)null;

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var token = new RunnerRegistrationToken(
            Guid.NewGuid(),
            tenantId,
            clientScope,
            hashes.Hash(secret),
            PatTokenLookupHash.Compute(secret),
            now,
            expiresAt,
            // Single use by default, because an enrollment secret that enrolls an unbounded number of hosts
            // is a larger thing to lose than one that enrolls a single host. It is not the only shape that
            // has to work, though: a fleet the platform scales for you spawns replicas nobody is present to
            // issue a token to, and one key per replica is not a ceremony an autoscaler can perform. A
            // bounded count allows a scaling group to be provisioned from one token, while the remaining
            // uses stay visible to the operator who issued it and the token can be revoked.
            maxUses,
            issuedByUserId);

        await registry.AddTokenAsync(token, ct);
        LogRegistrationTokenIssued(logger, token.Id, tenantId, clientScope.Count);

        return new RunnerRegistrationTokenIssue(token.Id, secret, token.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<bool> AssignClientScopeAsync(
        Guid runnerId,
        IReadOnlyList<Guid> clientScope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientScope);

        var runner = await registry.FindByIdAsync(runnerId, ct);
        if (runner is null)
        {
            return false;
        }

        // Nothing is done to the lease it may be holding. Narrowing a scope must not abandon a review that
        // is already half-finished; the new scope decides what it is offered next, which is where a scope
        // change belongs.
        runner.AssignClientScope(clientScope);
        await registry.UpdateAsync(runner, ct);
        LogClientScopeAssigned(logger, runnerId, clientScope.Count);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeRegistrationTokenAsync(Guid tokenId, CancellationToken ct = default)
    {
        var token = await registry.FindTokenByIdAsync(tokenId, ct);
        if (token is null)
        {
            return false;
        }

        token.Revoke(timeProvider.GetUtcNow());
        await registry.UpdateTokenAsync(token, ct);
        LogRegistrationTokenRevoked(logger, tokenId);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(Guid runnerId, CancellationToken ct = default)
    {
        var runner = await registry.FindByIdAsync(runnerId, ct);
        if (runner is null)
        {
            return false;
        }

        runner.Revoke(timeProvider.GetUtcNow());
        await registry.UpdateAsync(runner, ct);
        LogRunnerRevoked(logger, runnerId);
        return true;
    }

    /// <inheritdoc />
    public async Task<RunnerDeletionOutcome> DeleteAsync(Guid runnerId, CancellationToken ct = default)
    {
        var runner = await registry.FindByIdAsync(runnerId, ct);
        if (runner is null)
        {
            return RunnerDeletionOutcome.NotFound;
        }

        // Refused rather than cascaded. A lease this identity holds is still being renewed against it,
        // and the honest sequence for a live-but-unwanted host is revoke (its calls start failing), wait
        // out the lease, then delete. A stale row, which is the case this exists for, holds no lease.
        if (await registry.HoldsLeaseAsync(runnerId, ct))
        {
            return RunnerDeletionOutcome.HoldingLease;
        }

        var deleted = await registry.DeleteAsync(runnerId, ct);
        if (!deleted)
        {
            return RunnerDeletionOutcome.NotFound;
        }

        LogRunnerDeleted(logger, runnerId);
        return RunnerDeletionOutcome.Deleted;
    }

    /// <summary>
    ///     Mints a credential: a high-entropy secret, its indexed lookup hash, and the verifiable hash that
    ///     is all the database keeps. The secret is returned once and never stored, so an operator who loses
    ///     it renews rather than reads it back.
    /// </summary>
    private (string Secret, string CredentialHash, string LookupHash) IssueCredential()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (secret, hashes.Hash(secret), PatTokenLookupHash.Compute(secret));
    }

    // Every one of these deliberately omits the token and the credential. A secret in a log is a secret
    // that has left the system, and nothing recoverable from these lines identifies one.
    [LoggerMessage(
        EventId = 5511, Level = LogLevel.Information,
        Message = "Issued runner registration token {TokenId} for tenant {TenantId} scoped to {ClientScopeCount} client(s)")]
    private static partial void LogRegistrationTokenIssued(ILogger logger, Guid tokenId, Guid tenantId, int clientScopeCount);

    [LoggerMessage(EventId = 5513, Level = LogLevel.Information, Message = "Runner registration token {TokenId} was revoked")]
    private static partial void LogRegistrationTokenRevoked(ILogger logger, Guid tokenId);

    [LoggerMessage(EventId = 5512, Level = LogLevel.Information, Message = "Runner {RunnerId} was re-scoped to {ClientScopeCount} client(s)")]
    private static partial void LogClientScopeAssigned(ILogger logger, Guid runnerId, int clientScopeCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runner registration refused: the token was not valid")]
    private static partial void LogRegistrationRefused(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runner {RunnerId} credential renewal refused")]
    private static partial void LogRenewalRefused(ILogger logger, Guid runnerId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Runner {RunnerId} enrolled in tenant {TenantId} scoped to {ClientScopeCount} client(s)")]
    private static partial void LogRunnerEnrolled(ILogger logger, Guid runnerId, Guid tenantId, int clientScopeCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerId} revoked")]
    private static partial void LogRunnerRevoked(ILogger logger, Guid runnerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerId} deleted from the registry")]
    private static partial void LogRunnerDeleted(ILogger logger, Guid runnerId);
}
