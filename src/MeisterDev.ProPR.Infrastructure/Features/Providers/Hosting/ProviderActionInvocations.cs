// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>The states one run of an add-in action can be in.</summary>
public static class ProviderInvocationState
{
    /// <summary>Started, and the add-in has not reported.</summary>
    public const string Pending = "Pending";

    /// <summary>The add-in reported that its work finished.</summary>
    public const string Completed = "Completed";

    /// <summary>The add-in reported that its work failed.</summary>
    public const string Failed = "Failed";

    /// <summary>The window closed with nothing reported.</summary>
    public const string Expired = "Expired";

    /// <summary>The states an invocation can end in.</summary>
    public static readonly FrozenSet<string> Terminal =
        new[] { Completed, Failed, Expired }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every state a row may hold, which is the pending one and the terminal ones.</summary>
    public static readonly FrozenSet<string> All =
        new[] { Pending, Completed, Failed, Expired }.ToFrozenSet(StringComparer.Ordinal);
}

/// <summary>One run of an add-in action, as the host holds it.</summary>
/// <param name="Id">The invocation.</param>
/// <param name="ConnectionProfileId">The connection it acts on, or null once that connection has been deleted.</param>
/// <param name="ConnectionDisplayName">The connection's name as it stood when the run was opened.</param>
/// <param name="AddInKey">The family whose action it is.</param>
/// <param name="ActionId">The action, as the family declared it.</param>
/// <param name="InitiatingAdminId">The administrator who started it.</param>
/// <param name="State">Where it stands.</param>
/// <param name="TerminalMessage">What the family said when it finished, capped and scrubbed by the host.</param>
/// <param name="WaitingFor">
///     What the run is waiting for, composed by the host when it opened the invocation. It is what an operator
///     reads while the run is pending, and it becomes the terminal message when the window closes on it.
/// </param>
/// <param name="ExpiresAt">The end of the bounded window.</param>
public sealed record ProviderActionInvocation(
    Guid Id,
    Guid? ConnectionProfileId,
    string ConnectionDisplayName,
    string AddInKey,
    string ActionId,
    Guid? InitiatingAdminId,
    string State,
    string? TerminalMessage,
    string? WaitingFor,
    DateTimeOffset ExpiresAt)
{
    /// <summary>Whether the run has reached a state nothing moves it out of.</summary>
    public bool IsTerminal => this.State != ProviderInvocationState.Pending;
}

/// <summary>
///     Opens, reads and closes the record of an operator-started add-in action.
/// </summary>
/// <remarks>
///     <para>
///         A record rather than a held request, because an action's work outlives the call that started it. An
///         operator signing in at a vendor takes minutes and the result arrives on something other than the
///         request that began it, so there is no call left to answer on. The family writes its terminal state
///         here instead, and whatever is watching the action reads it back.
///     </para>
///     <para>
///         The window is what keeps an invocation from being pending for ever. An invocation still pending past
///         it is expired, not left open, so an operator whose browser never reached the family's listener is told
///         what the invocation was waiting for rather than watching it never resolve.
///     </para>
/// </remarks>
/// <param name="contextFactory">Opens a context per call, independent of whatever built this.</param>
/// <param name="timeProvider">Supplies the instant windows are measured from.</param>
public sealed class ProviderActionInvocations(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    TimeProvider timeProvider)
{
    /// <summary>Opens an invocation against one connection.</summary>
    /// <param name="binding">The connection the action acts on and the family serving it.</param>
    /// <param name="actionId">The action, as the family declared it.</param>
    /// <param name="initiatingAdminId">
    ///     The administrator starting the action. Whatever the action produces is recorded against this
    ///     administrator and never against whoever completed it: a grant is personal, and the disconnect path
    ///     reads the owner in order to revoke it.
    /// </param>
    /// <param name="window">How long the invocation may stay open, bounded by what the host allows.</param>
    /// <param name="waitingFor">
    ///     What the run is waiting for, in terms an operator can act on. Stored when the invocation is opened,
    ///     because the sweep that expires a window is one statement over every row it finds and has nothing
    ///     per-row to say by the time it runs.
    /// </param>
    /// <param name="ct">Cancels the write.</param>
    public async Task<ProviderActionInvocation> OpenAsync(
        ProviderAddInBinding binding,
        string actionId,
        Guid? initiatingAdminId,
        TimeSpan window,
        string? waitingFor = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        if (actionId.Length > ProviderHostLimits.MaximumActionIdLength)
        {
            throw new ProviderRequestRejectedException(
                $"The action identifier is {actionId.Length} characters; the host records at most "
                + $"{ProviderHostLimits.MaximumActionIdLength}.",
                nameof(actionId));
        }

        var now = timeProvider.GetUtcNow();
        var bounded = window <= TimeSpan.Zero || window > ProviderHostLimits.MaximumInvocationWindow
            ? ProviderHostLimits.MaximumInvocationWindow
            : window;

        var record = new ProviderActionInvocationRecord
        {
            Id = Guid.NewGuid(),
            ConnectionProfileId = binding.ConnectionProfileId,
            ConnectionDisplayName = binding.ConnectionDisplayName,
            AddInKey = binding.AddInKey,
            ActionId = actionId,
            InitiatingAdminId = initiatingAdminId,
            State = ProviderInvocationState.Pending,
            WaitingFor = ProviderMessageGuard.Sanitize(waitingFor) is { Length: > 0 } stated ? stated : null,
            CreatedAt = now,
            ExpiresAt = now + bounded,
        };

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Every open takes the family's finished runs past the retention window with it. Swept on the open path
        // rather than by a loop of its own, for the reason the keyed store sweeps there: a family whose actions
        // nobody starts accumulates nothing, and the work is bounded by what that one family left behind. It is
        // also the only thing that clears a run whose connection has been deleted, because the row outlives the
        // connection on purpose and no other sweep reaches it.
        await SweepAsync(db, binding.AddInKey, now, ct).ConfigureAwait(false);

        db.ProviderActionInvocations.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return ToInvocation(record);
    }

    /// <summary>Reads one invocation, expiring it first when its window has closed.</summary>
    /// <param name="invocationId">The invocation to read.</param>
    /// <param name="ct">Cancels the read.</param>
    public async Task<ProviderActionInvocation?> GetAsync(Guid invocationId, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await ExpireDueAsync(db, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);

        var record = await db.ProviderActionInvocations
            .AsNoTracking()
            .FirstOrDefaultAsync(invocation => invocation.Id == invocationId, ct)
            .ConfigureAwait(false);

        return record is null ? null : ToInvocation(record);
    }

    /// <summary>
    ///     Writes the terminal state of one invocation, if it is still pending.
    /// </summary>
    /// <remarks>
    ///     Write-once by the same predicate that writes it, so a family reporting twice does not overwrite what
    ///     it said first, and neither a family nor an expiry can move an invocation out of a state it already
    ///     reached. The window is part of that predicate: a run past its window is expired whether or not the
    ///     sweep that marks it has run yet, and an answer arriving after it has nothing left to close. Checking
    ///     the window outside the statement would leave the interval between the check and the write open, which
    ///     is the interval a late answer arrives in.
    /// </remarks>
    /// <param name="invocationId">The invocation to close.</param>
    /// <param name="state">The terminal state.</param>
    /// <param name="message">What to show the operator, already capped and scrubbed.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns><see langword="true" /> when this call was the one that closed it.</returns>
    public async Task<bool> TryCloseAsync(
        Guid invocationId,
        string state,
        string message,
        CancellationToken ct = default)
    {
        // Only a state the invocation can end in. The caller's string was written straight to the row, so a
        // caller passing Pending, or anything at all, produced an invocation stamped with a completion time and
        // a state that says it never completed.
        if (!ProviderInvocationState.Terminal.Contains(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                $"An invocation closes in one of: {string.Join(", ", ProviderInvocationState.Terminal)}.");
        }

        var now = timeProvider.GetUtcNow();

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var closed = await db.ProviderActionInvocations
            .Where(invocation => invocation.Id == invocationId
                                 && invocation.State == ProviderInvocationState.Pending
                                 && invocation.ExpiresAt > now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(invocation => invocation.State, state)
                    .SetProperty(invocation => invocation.TerminalMessage, message)
                    .SetProperty(invocation => invocation.CompletedAt, now),
                ct)
            .ConfigureAwait(false);

        return closed > 0;
    }

    /// <summary>
    ///     Expires the named invocations even though their windows are still open, which the host does
    ///     for the runs it started when the process stops.
    /// </summary>
    /// <remarks>
    ///     Scoped to the identifiers the caller supplies rather than to every pending row, because a deployment
    ///     runs more than one host and a run another host opened is still live when this one stops.
    /// </remarks>
    /// <param name="invocationIds">The runs this host opened and has not closed.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <returns>How many were expired by this call.</returns>
    /// <summary>Removes the finished runs of one family that are past the retention window.</summary>
    /// <remarks>
    ///     A run that never reached a terminal state is left alone whatever its age: it is still pending to
    ///     whoever reads it, and expiring one is what the window sweep does. The instant judged is when the run
    ///     finished, falling back to when its window closed for a row that reached a terminal state without one
    ///     recorded.
    /// </remarks>
    /// <param name="db">The context to sweep in.</param>
    /// <param name="addInKey">The family whose runs are swept.</param>
    /// <param name="now">The instant the window is measured back from.</param>
    /// <param name="ct">Cancels the sweep.</param>
    internal static Task<int> SweepAsync(
        MeisterProPRDbContext db,
        string addInKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var cutoff = now - ProviderHostLimits.InvocationHistoryRetention;

        return db.ProviderActionInvocations
            .Where(invocation => invocation.AddInKey == addInKey
                                 && invocation.State != ProviderInvocationState.Pending
                                 && (invocation.CompletedAt ?? invocation.ExpiresAt) < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> ExpireAsync(IReadOnlyCollection<Guid> invocationIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocationIds);

        if (invocationIds.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var named = invocationIds.ToArray();

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await db.ProviderActionInvocations
            .Where(invocation => named.Contains(invocation.Id)
                                 && invocation.State == ProviderInvocationState.Pending)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(invocation => invocation.State, ProviderInvocationState.Expired)
                    .SetProperty(invocation => invocation.TerminalMessage, invocation => invocation.WaitingFor)
                    .SetProperty(invocation => invocation.CompletedAt, now),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Expires every invocation still pending past its window.</summary>
    /// <remarks>
    ///     The message an expired run carries is what the host recorded it was waiting for when it opened it.
    ///     This is one statement over every row whose window has closed, so there is nothing per-row to compose
    ///     here; composing it at the start is what gives an operator a reason rather than a bare timeout.
    /// </remarks>
    /// <param name="db">The context to write in.</param>
    /// <param name="now">The instant windows are measured against.</param>
    /// <param name="ct">Cancels the write.</param>
    internal static Task<int> ExpireDueAsync(MeisterProPRDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        return db.ProviderActionInvocations
            .Where(invocation => invocation.State == ProviderInvocationState.Pending && invocation.ExpiresAt <= now)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(invocation => invocation.State, ProviderInvocationState.Expired)
                    .SetProperty(invocation => invocation.TerminalMessage, invocation => invocation.WaitingFor)
                    .SetProperty(invocation => invocation.CompletedAt, now),
                ct);
    }

    private static ProviderActionInvocation ToInvocation(ProviderActionInvocationRecord record)
    {
        return new ProviderActionInvocation(
            record.Id,
            record.ConnectionProfileId,
            record.ConnectionDisplayName,
            record.AddInKey,
            record.ActionId,
            record.InitiatingAdminId,
            record.State,
            record.TerminalMessage,
            record.WaitingFor,
            record.ExpiresAt);
    }
}
