// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Imports model-catalog snapshots. Import writes global entries only, never a tenant or client override, so
///     a refresh can land without disturbing the values an operator or a negotiated contract set.
/// </summary>
public interface IModelCatalogImportService
{
    /// <summary>
    ///     Imports the snapshot bundled with the application. Safe to run on every startup: entries are upserted
    ///     by provider and model, so a second run updates rather than duplicates, and it makes a fresh
    ///     installation's catalog populated without any operator action.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many global entries were written or updated.</returns>
    Task<int> SeedFromBundledSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    ///     Imports an operator-supplied snapshot, which is how a running installation moves to newer model data
    ///     without a redeploy and without the application fetching anything itself.
    /// </summary>
    /// <param name="snapshot">The raw snapshot content.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many global entries were written or updated.</returns>
    Task<int> ImportSnapshotAsync(Stream snapshot, CancellationToken ct = default);
}
