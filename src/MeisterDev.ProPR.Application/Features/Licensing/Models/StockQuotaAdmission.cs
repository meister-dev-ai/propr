// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     One decision about whether another of a counted resource fits, together with the transaction it was
///     decided in.
///     <para>
///         A refusal carries the ceiling, where the ceiling came from, and the number the installation held when
///         the decision was made, so a caller can report the licensed number and the observed count without
///         reading either again. An admission carries the same numbers, so a caller logs one thing whichever way
///         the decision went.
///     </para>
///     <para>
///         The instance owns the transaction the decision was made in, when the decision needed one. A caller
///         commits it after the creation has been saved and disposes it either way; disposing an admission that
///         was not committed discards whatever was written in that transaction.
///     </para>
/// </summary>
public sealed class StockQuotaAdmission : IAsyncDisposable
{
    private readonly IStockQuotaAdmissionScope? _scope;

    private StockQuotaAdmission(
        bool isAdmitted,
        LicenseLimitResolution limit,
        long? currentCount,
        IStockQuotaAdmissionScope? scope)
    {
        this.IsAdmitted = isAdmitted;
        this.Limit = limit;
        this.CurrentCount = currentCount;
        this._scope = scope;
    }

    /// <summary>Whether one more may be created.</summary>
    public bool IsAdmitted { get; }

    /// <summary>
    ///     The ceiling the decision was made against, with the source and the lifecycle stage it was resolved
    ///     under.
    /// </summary>
    public LicenseLimitResolution Limit { get; }

    /// <summary>
    ///     How many the installation held when the decision was made, or null when nothing was counted because
    ///     the ceiling bounds nothing. Null is distinct from zero, which is a count of an empty installation.
    /// </summary>
    public long? CurrentCount { get; }

    /// <summary>An admission, optionally holding the transaction it was decided in.</summary>
    /// <param name="limit">The ceiling the decision was made against.</param>
    /// <param name="currentCount">
    ///     How many the installation held, or null when the ceiling made a count unnecessary.
    /// </param>
    /// <param name="scope">
    ///     The transaction the decision was made in, or null when the decision needed none and when the caller
    ///     owns the transaction it was made in.
    /// </param>
    /// <returns>The admission.</returns>
    public static StockQuotaAdmission Admitted(
        LicenseLimitResolution limit,
        long? currentCount = null,
        IStockQuotaAdmissionScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(limit);

        return new StockQuotaAdmission(true, limit, currentCount, scope);
    }

    /// <summary>
    ///     A refusal. It holds no transaction: nothing was written, so whatever the decision was made in has
    ///     already ended or belongs to the caller.
    /// </summary>
    /// <param name="limit">The ceiling the creation was refused against.</param>
    /// <param name="currentCount">How many the installation held.</param>
    /// <returns>The refusal.</returns>
    public static StockQuotaAdmission Refused(LicenseLimitResolution limit, long currentCount)
    {
        ArgumentNullException.ThrowIfNull(limit);

        return new StockQuotaAdmission(false, limit, currentCount, null);
    }

    /// <summary>
    ///     Ends the transaction the decision was made in, keeping the creation it admitted. Does nothing when
    ///     the admission holds no transaction.
    /// </summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>A task that completes when the transaction has ended.</returns>
    public Task CommitAsync(CancellationToken cancellationToken = default) =>
        this._scope?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => this._scope?.DisposeAsync() ?? ValueTask.CompletedTask;
}
