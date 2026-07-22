// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Resolves a tenant's aggregate USD spend for the current period plus a trailing per-month trend, summed
///     across every client in the tenant. Read-only governance surface; it never enforces or mutates.
/// </summary>
public interface ITenantBudgetSpendService
{
    /// <summary>
    ///     Returns the tenant's aggregate current-period spend, the summed client caps, a trajectory projection, and
    ///     a trailing <paramref name="monthsBack" />-month per-month trend.
    /// </summary>
    Task<TenantSpendDto> GetSpendAsync(Guid tenantId, int monthsBack, CancellationToken ct = default);
}
