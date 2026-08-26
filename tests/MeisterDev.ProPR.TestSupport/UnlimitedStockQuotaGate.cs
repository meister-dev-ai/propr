// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.TestSupport;

/// <summary>
///     A stock-quota gate that admits everything asked of it, holding no transaction.
///     <para>
///         A site that asks the gate takes it as a required dependency, and a test composition that leaves the
///         licensing module out has none to inject. This stands in for it and decides as an unlimited ceiling
///         does, so a test that is not about a quota sees no ceiling.
///     </para>
/// </summary>
public sealed class UnlimitedStockQuotaGate : IStockQuotaGate
{
    /// <inheritdoc />
    public Task<StockQuotaAdmission> AdmitOneAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(StockQuotaAdmission.Admitted(LicenseLimitResolution.Unlimited(key, LicenseLimitSource.Community, LicenseStage.None)));

    /// <inheritdoc />
    public Task<StockQuotaAdmission> AdmitExistingAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default) =>
        this.AdmitOneAsync(key, cancellationToken);
}
