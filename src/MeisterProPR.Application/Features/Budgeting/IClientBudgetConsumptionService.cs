// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Resolves a client's spend against its monthly budget for the current period, together with a trajectory
///     projection. Read-only governance surface (the FinOps half of budgeting); it never enforces or mutates.
/// </summary>
public interface IClientBudgetConsumptionService
{
    /// <summary>
    ///     Returns the consumption for <paramref name="clientId" /> for a calendar-month period. When
    ///     <paramref name="year" /> and <paramref name="month" /> are null the current month is used and a trajectory
    ///     forecast is included; for a past month the full-month actuals are returned with no forecast (the month is
    ///     complete). Caps always reflect the current configuration (caps are not snapshotted historically).
    /// </summary>
    Task<ClientBudgetConsumptionDto> GetConsumptionAsync(
        Guid clientId,
        int? year = null,
        int? month = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the client's estimated USD spend per calendar month over the trailing <paramref name="monthsBack" />
    ///     months (including the current, in-progress month), with the currently configured monthly caps for
    ///     comparison.
    /// </summary>
    Task<ClientBudgetHistoryDto> GetHistoryAsync(Guid clientId, int monthsBack, CancellationToken ct = default);
}
