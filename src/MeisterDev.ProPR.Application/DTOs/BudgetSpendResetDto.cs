// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One manual spend reset an administrator performed on a client's monthly period: the extra allowance it
///     granted and the audit record of who granted it and what the period's ceiling was before and after. The
///     recorded caps describe the moment of the reset and do not change if the configured cap is edited later.
/// </summary>
/// <param name="Id">The reset identifier.</param>
/// <param name="PeriodStart">Inclusive first day of the monthly period the reset applies to (UTC).</param>
/// <param name="TopUpSoftCapUsd">The monthly soft-cap allowance granted, or null when no soft cap was configured.</param>
/// <param name="TopUpHardCapUsd">The monthly hard-cap allowance granted, or null when no hard cap was configured.</param>
/// <param name="EffectiveSoftCapBeforeUsd">The period's effective soft cap immediately before the reset.</param>
/// <param name="EffectiveSoftCapAfterUsd">The period's effective soft cap immediately after the reset.</param>
/// <param name="EffectiveHardCapBeforeUsd">The period's effective hard cap immediately before the reset.</param>
/// <param name="EffectiveHardCapAfterUsd">The period's effective hard cap immediately after the reset.</param>
/// <param name="ActorUserId">The administrator who performed the reset, or null when unresolved.</param>
/// <param name="ActorUsername">The administrator's username when it could be resolved, otherwise null.</param>
/// <param name="PerformedAt">When the reset was performed (UTC).</param>
public sealed record BudgetSpendResetDto(
    Guid Id,
    DateOnly PeriodStart,
    decimal? TopUpSoftCapUsd,
    decimal? TopUpHardCapUsd,
    decimal? EffectiveSoftCapBeforeUsd,
    decimal? EffectiveSoftCapAfterUsd,
    decimal? EffectiveHardCapBeforeUsd,
    decimal? EffectiveHardCapAfterUsd,
    Guid? ActorUserId,
    string? ActorUsername,
    DateTime PerformedAt);
