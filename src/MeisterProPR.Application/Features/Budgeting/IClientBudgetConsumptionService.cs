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
    ///     Returns the current-period consumption and forecast for <paramref name="clientId" />, computed as of the
    ///     current UTC date. Spend and caps reflect the calendar-month period, which resets at the month boundary.
    /// </summary>
    Task<ClientBudgetConsumptionDto> GetConsumptionAsync(Guid clientId, CancellationToken ct = default);
}
