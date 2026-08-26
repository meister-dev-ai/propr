// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     The database transaction one stock-quota admission was decided in, as the admission holds it.
///     <para>
///         The serialization a stock quota is admitted under lasts for a transaction, so the transaction has to
///         outlive the decision and end only once the creation it admitted has been saved. The transaction type
///         belongs to the persistence layer, so an admission holds it through this port.
///     </para>
/// </summary>
public interface IStockQuotaAdmissionScope : IAsyncDisposable
{
    /// <summary>Ends the transaction, keeping what was written in it.</summary>
    /// <param name="cancellationToken">Cancels the commit.</param>
    /// <returns>A task that completes when the transaction has ended.</returns>
    Task CommitAsync(CancellationToken cancellationToken = default);
}
