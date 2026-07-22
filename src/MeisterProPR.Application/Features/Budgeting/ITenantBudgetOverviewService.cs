// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Resolves a tenant-wide view of current-period spend against budget for every client in the tenant. Read-only
///     governance surface (the FinOps half of budgeting); it never enforces or mutates.
/// </summary>
public interface ITenantBudgetOverviewService
{
    /// <summary>
    ///     Returns each client in <paramref name="tenantId" /> with its current calendar-month spend, configured
    ///     monthly caps, and a trajectory projection, ordered by spend-to-date descending.
    /// </summary>
    Task<TenantBudgetOverviewDto> GetOverviewAsync(Guid tenantId, CancellationToken ct = default);
}
