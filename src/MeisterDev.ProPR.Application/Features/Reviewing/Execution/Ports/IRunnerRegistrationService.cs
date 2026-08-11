// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Enrollment, credential lifecycle, and authentication for review executors.</summary>
public interface IRunnerRegistrationService
{
    /// <summary>Enrolls a host presenting an operator-issued registration token.</summary>
    Task<RunnerRegistrationResult> RegisterAsync(RunnerRegistrationRequest request, CancellationToken ct = default);

    /// <summary>Issues a fresh credential to an already-enrolled runner, keeping its identity and scope.</summary>
    Task<RunnerRegistrationResult> RenewCredentialAsync(
        Guid runnerId,
        string currentCredential,
        int contractVersion,
        CancellationToken ct = default);

    /// <summary>Resolves a presented credential to its runner, or null when it is not usable.</summary>
    Task<ReviewRunner?> AuthenticateAsync(string credential, CancellationToken ct = default);

    /// <summary>Revokes a runner so it can no longer lease.</summary>
    Task<bool> RevokeAsync(Guid runnerId, CancellationToken ct = default);

    /// <summary>
    ///     Mints a single-use registration token for an operator to hand to a host. The token value is
    ///     returned exactly once and never stored in a form it can be read back from, so an operator who
    ///     loses it issues another rather than recovering this one.
    /// </summary>
    /// <param name="tenantId">The tenant the enrolled runner will belong to.</param>
    /// <param name="clientScope">The clients it may serve. Empty means every client in the tenant.</param>
    /// <param name="validFor">How long the token may be used for, or null for one that does not expire.</param>
    /// <param name="issuedByUserId">The operator issuing it, recorded for the audit.</param>
    /// <param name="maxUses">
    ///     How many hosts may enroll with it, or null for no limit. One is the safe default for a host
    ///     enrolled by hand. A scaling group needs more than one, because the replicas it starts have no
    ///     operator present to issue them a token each.
    /// </param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerRegistrationTokenIssue> IssueRegistrationTokenAsync(
        Guid tenantId,
        IReadOnlyList<Guid> clientScope,
        TimeSpan? validFor,
        Guid issuedByUserId,
        int? maxUses = 1,
        CancellationToken ct = default);

    /// <summary>
    ///     Re-stamps which clients a runner may serve. Takes effect on its next lease rather than on the
    ///     one it holds: narrowing a scope must not abandon a review already half-finished.
    /// </summary>
    /// <param name="runnerId">The runner.</param>
    /// <param name="clientScope">The new scope. Empty means every client in the tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<bool> AssignClientScopeAsync(Guid runnerId, IReadOnlyList<Guid> clientScope, CancellationToken ct = default);

    /// <summary>
    ///     Revokes an issued registration token so it can no longer enroll anything. The counterpart to
    ///     issuing one: without it a token that leaked before it was used stays valid for its whole
    ///     lifetime and an operator has no recourse but to wait it out.
    /// </summary>
    /// <param name="tokenId">The token to revoke.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<bool> RevokeRegistrationTokenAsync(Guid tokenId, CancellationToken ct = default);

    /// <summary>
    ///     Removes a runner's row entirely. Without this, every re-enrollment creates a new identity and the
    ///     old rows are permanent: they count toward the registered-runner total, keep the installation off
    ///     the no-runners fast path, and stay in the fleet view as entries that cannot be told apart from a
    ///     live host that has stopped making progress. Refused while the runner still holds a lease, because
    ///     deleting the identity under a running job would orphan work the lease machinery is still tracking.
    /// </summary>
    /// <param name="runnerId">The runner to delete.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerDeletionOutcome> DeleteAsync(Guid runnerId, CancellationToken ct = default);
}

/// <summary>What deleting a runner produced.</summary>
public enum RunnerDeletionOutcome
{
    /// <summary>The row is gone.</summary>
    Deleted = 0,

    /// <summary>No runner with that identity exists.</summary>
    NotFound = 1,

    /// <summary>
    ///     The runner still holds a lease. Let it finish, let the lease expire, or revoke the runner first
    ///     so it stops renewing, then delete.
    /// </summary>
    HoldingLease = 2,
}

/// <summary>Persistence for runners and the tokens that enroll them.</summary>
public interface IRunnerRegistry
{
    /// <summary>Finds a registration token by its indexed lookup hash.</summary>
    Task<RunnerRegistrationToken?> FindTokenAsync(string tokenLookupHash, CancellationToken ct = default);

    /// <summary>Finds a runner by its indexed credential lookup hash.</summary>
    Task<ReviewRunner?> FindByCredentialLookupAsync(string credentialLookupHash, CancellationToken ct = default);

    /// <summary>Finds a runner by identity.</summary>
    Task<ReviewRunner?> FindByIdAsync(Guid runnerId, CancellationToken ct = default);

    /// <summary>Persists a newly enrolled runner and the token use that enrolled it, together.</summary>
    Task AddAsync(ReviewRunner runner, RunnerRegistrationToken token, CancellationToken ct = default);

    /// <summary>Persists changes to a runner.</summary>
    Task UpdateAsync(ReviewRunner runner, CancellationToken ct = default);

    /// <summary>Persists a newly issued registration token that has not enrolled anything yet.</summary>
    Task AddTokenAsync(RunnerRegistrationToken token, CancellationToken ct = default);

    /// <summary>Every runner in one tenant, whatever its state, for the operator registry.</summary>
    Task<IReadOnlyList<ReviewRunner>> ListAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>Registration tokens issued for one tenant that have not yet expired or been used up.</summary>
    Task<IReadOnlyList<RunnerRegistrationToken>> ListTokensAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Runners across every tenant that have not called in since <paramref name="unseenSince" />, oldest
    ///     first. These are the hosts a prune sweep treats as gone.
    ///     <para>
    ///         A host that never called in at all is judged by when it enrolled, because a runner that
    ///         enrolled and immediately died is exactly the row worth reaping and has no last-seen to
    ///         measure.
    ///     </para>
    /// </summary>
    /// <param name="unseenSince">The cutoff. A runner last seen before this is a candidate.</param>
    /// <param name="limit">Most candidates to return in one sweep.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<IReadOnlyList<Guid>> ListUnseenSinceAsync(DateTimeOffset unseenSince, int limit, CancellationToken ct = default);

    /// <summary>Finds a registration token by identity.</summary>
    Task<RunnerRegistrationToken?> FindTokenByIdAsync(Guid tokenId, CancellationToken ct = default);

    /// <summary>Persists changes to a registration token.</summary>
    Task UpdateTokenAsync(RunnerRegistrationToken token, CancellationToken ct = default);

    /// <summary>Whether the runner currently holds any executing job's lease.</summary>
    Task<bool> HoldsLeaseAsync(Guid runnerId, CancellationToken ct = default);

    /// <summary>Removes a runner's row. Returns false when no such runner exists.</summary>
    Task<bool> DeleteAsync(Guid runnerId, CancellationToken ct = default);
}
