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
    ILicensingCapabilityService? licensing = null,
    IStockQuotaGate? stockQuotaGate = null) : IRunnerRegistrationService
{
    /// <summary>The refusal a host presenting an unusable registration token is given, wherever that is decided.</summary>
    private const string UnusableTokenRefusal = "The registration token is not valid.";

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
            return RunnerRegistrationResult.Refused(UnusableTokenRefusal);
        }

        // Minted before the admission. Hashing the secret costs on the order of a hundred milliseconds of
        // CPU, and doing it inside the admission would hold the installation-wide lock for that long on every
        // enrollment, so a fleet coming back would enroll one host at a time. It depends on nothing the
        // admission provides, and a refused enrollment discards it.
        var (secret, credentialHash, lookupHash) = this.IssueCredential();

        // Asked for before the token is spent, so a refusal leaves the token usable for a later attempt. The
        // admission holds the transaction the spent use and the enrollment below are saved in, because the
        // registry writes both through the same scoped context.
        await using var admission = stockQuotaGate is null
            ? null
            : await stockQuotaGate.AdmitOneAsync(LicenseLimitKey.Runners, ct).ConfigureAwait(false);
        if (admission is { IsAdmitted: false })
        {
            LogEnrollmentRefusedAtCeiling(logger, admission.CurrentCount, admission.Limit.Count);
            return RunnerRegistrationResult.Refused(DescribeRunnerCeilingRefusal(admission));
        }

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

        // The use is spent where the count is kept, by the statement that checks one is left. The check above
        // was made on a token loaded before the admission, and two enrollments presenting one token load the
        // same count and both pass it, so a single-use token would enroll both. A token that has nothing left
        // by the time it is spent is refused in the same words as any other unusable one.
        if (!await registry.TryAddAsync(runner, token, now, ct))
        {
            LogRegistrationRefused(logger);
            return RunnerRegistrationResult.Refused(UnusableTokenRefusal);
        }

        // After the save, because committing ends the transaction the save was written in. An admission disposed
        // without a commit discards the enrollment and the spent use with it.
        if (admission is not null)
        {
            await admission.CommitAsync(ct).ConfigureAwait(false);
        }

        LogRunnerEnrolled(logger, runner.Id, runner.TenantId, runner.ClientScope.Count);
        return RunnerRegistrationResult.Enrolled(runner.Id, secret, runner.CredentialExpiresAt);
    }

    /// <summary>
    ///     The refusal an enrolling host is told. An enrollment is refused at the ceiling, where removing one
    ///     runner frees the place the new one needs, so that is the remedy it names.
    /// </summary>
    /// <param name="admission">The refused admission, which carries both numbers and their source.</param>
    /// <returns>The refusal.</returns>
    private static string DescribeRunnerCeilingRefusal(StockQuotaAdmission admission)
    {
        return DescribeRunnerCeilingRefusal(
            admission,
            "enrollment",
            "Enrolling another requires removing a runner, or a license that allows more.");
    }

    /// <summary>
    ///     The refusal a renewing host is told. A renewal is refused only once the count has passed the
    ///     ceiling, which is the state a lowered ceiling produces, so removing one runner is not necessarily
    ///     enough to bring the count back within it and the remedy names the count rather than one removal.
    /// </summary>
    /// <param name="admission">The refused admission, which carries both numbers and their source.</param>
    /// <returns>The refusal.</returns>
    private static string DescribeRunnerRenewalCeilingRefusal(StockQuotaAdmission admission)
    {
        return DescribeRunnerCeilingRefusal(
            admission,
            "credential renewal",
            "Renewing requires the count back within that number, by removing runners or by a license that allows more.");
    }

    /// <summary>
    ///     The refusal a host is told, naming the licensed number of registrations and how many runners the
    ///     installation can still be given work by. Each noun and verb agrees with the number in front of it,
    ///     because either can be one. Enrollment and renewal name the same two numbers and differ only in what
    ///     they say makes the next attempt succeed.
    ///     <para>
    ///         A runner ceiling can also come from the community values, which allow none. Naming a license there
    ///         would report a grant the installation does not have, so that case has its own wording.
    ///     </para>
    ///     <para>
    ///         A licensed ceiling of zero has its own wording as well. Removing a runner does not free a seat
    ///         under it, so the refusal does not suggest that.
    ///     </para>
    /// </summary>
    /// <param name="admission">The refused admission, which carries both numbers and their source.</param>
    /// <param name="refusedAction">What was refused, for the fault raised when there is no number to report.</param>
    /// <param name="remedy">The closing sentence, which says what makes the next attempt succeed.</param>
    /// <returns>The refusal.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The refusal carries no counted ceiling, which is a fault in the decision rather than a number to
    ///     report: a registration is only refused against a ceiling a count was compared with.
    /// </exception>
    private static string DescribeRunnerCeilingRefusal(
        StockQuotaAdmission admission,
        string refusedAction,
        string remedy)
    {
        if (admission.Limit.Source != LicenseLimitSource.License)
        {
            return "This installation is not entitled to register runners.";
        }

        if (admission.Limit.Count is not { } ceiling)
        {
            throw new InvalidOperationException(
                $"A runner {refusedAction} was refused against a {admission.Limit.Ceiling} ceiling, which carries no number to report.");
        }

        if (ceiling == 0)
        {
            return "The license in force allows no registered runners.";
        }

        var enrolled = admission.CurrentCount;

        // The number counted is the runners that can still be given work, so the message names that rather
        // than enrollment: a row whose credential has expired is not among them and removing it frees nothing.
        return $"The license in force allows {ceiling} registered {(ceiling == 1 ? "runner" : "runners")} "
               + $"and {enrolled} {(enrolled == 1 ? "holds" : "hold")} a current credential. "
               + remedy;
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

        // Minted before the admission for the same reason enrollment mints before its own: hashing the
        // secret costs on the order of a hundred milliseconds of CPU, and doing it inside the admission
        // would hold the installation-wide lock for that long.
        var (secret, credentialHash, lookupHash) = this.IssueCredential();

        // Renewal is where a lowered runner ceiling reaches the runners that are already enrolled. Without
        // this the count only bounds enrollment, and a fleet that was licensed for ten keeps all ten leasing
        // work and renewing indefinitely after the license drops to two.
        //
        // Asked as "does this one still fit" rather than "does one more fit". The renewing runner is inside
        // the count: only a runner whose credential is still current can authenticate, and authenticating is
        // how it reaches this call, so it is one of the enrolled runners counted here. Asking for one more
        // would refuse every renewal on a fleet sitting exactly at its ceiling and empty it within one
        // credential lifetime.
        await using var admission = stockQuotaGate is null
            ? null
            : await stockQuotaGate.AdmitExistingAsync(LicenseLimitKey.Runners, ct).ConfigureAwait(false);
        if (admission is { IsAdmitted: false })
        {
            LogRenewalRefusedAtCeiling(logger, runnerId, admission.CurrentCount, admission.Limit.Count);

            // Which runners keep working after a ceiling is lowered follows from the order they happen to
            // renew in, and nothing chooses between them beyond that. A refused runner keeps the credential
            // it already holds until that credential expires, so none is stopped while it is executing a
            // review, and the fleet is back within the licensed number within one credential lifetime.
            return RunnerRegistrationResult.Refused(DescribeRunnerRenewalCeilingRefusal(admission));
        }

        // Same identity, same scope. Renewal exists so a credential can expire without an operator having to
        // enroll the host again, and re-stamping the scope here would undo an operator's change.
        runner.RenewCredential(
            credentialHash,
            lookupHash,
            timeProvider.GetUtcNow() + LifetimeFor(runner.TenantId),
            contractVersion);
        await registry.UpdateAsync(runner, ct);

        // After the save, because committing ends the transaction the save was written in. An admission
        // disposed without a commit discards the renewal.
        if (admission is not null)
        {
            await admission.CommitAsync(ct).ConfigureAwait(false);
        }

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
            // is more damaging to lose than one that enrolls a single host. It is not the only case that has
            // to work: a fleet scaled by the platform starts replicas with no operator present to issue a
            // token for each one, and an autoscaler cannot issue one key per replica. A bounded count lets a
            // scaling group be provisioned from one token, while the remaining uses stay visible to the
            // operator who issued it and the token can be revoked.
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

    // Every one of these omits the token and the credential. A secret in a log is a secret
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

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Runner registration refused: {Enrolled} enrolled against a ceiling of {Ceiling}")]
    private static partial void LogEnrollmentRefusedAtCeiling(ILogger logger, long? enrolled, long? ceiling);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runner {RunnerId} credential renewal refused")]
    private static partial void LogRenewalRefused(ILogger logger, Guid runnerId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Runner {RunnerId} credential renewal refused: {Enrolled} hold a current credential against a ceiling of {Ceiling}")]
    private static partial void LogRenewalRefusedAtCeiling(ILogger logger, Guid runnerId, long? enrolled, long? ceiling);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Runner {RunnerId} enrolled in tenant {TenantId} scoped to {ClientScopeCount} client(s)")]
    private static partial void LogRunnerEnrolled(ILogger logger, Guid runnerId, Guid tenantId, int clientScopeCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerId} revoked")]
    private static partial void LogRunnerRevoked(ILogger logger, Guid runnerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Runner {RunnerId} deleted from the registry")]
    private static partial void LogRunnerDeleted(ILogger logger, Guid runnerId);
}
