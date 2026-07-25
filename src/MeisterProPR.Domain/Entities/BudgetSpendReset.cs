// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     A manual spend reset an administrator performed on a client's monthly budget period. The row is both the
///     durable state and the audit entry: the top-up amounts grant a fresh allowance on top of what the period has
///     already consumed, while the before/after effective caps record what the reset changed at the moment it was
///     performed. Spend history is never rewritten — a reset only raises the period's ceiling.
/// </summary>
public sealed class BudgetSpendReset
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The client whose budget period was reset.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Inclusive first day of the monthly period the reset applies to (UTC).</summary>
    public DateOnly PeriodStart { get; set; }

    /// <summary>
    ///     The monthly soft-cap allowance this reset granted, or null when no soft cap was configured at the time.
    ///     Snapshotted: a later edit to the configured cap never changes what this reset granted.
    /// </summary>
    public decimal? TopUpSoftCapUsd { get; set; }

    /// <summary>
    ///     The monthly hard-cap allowance this reset granted, or null when no hard cap was configured at the time.
    ///     Snapshotted, like <see cref="TopUpSoftCapUsd" />.
    /// </summary>
    public decimal? TopUpHardCapUsd { get; set; }

    /// <summary>The period's effective monthly soft cap immediately before the reset, or null when uncapped.</summary>
    public decimal? EffectiveSoftCapBeforeUsd { get; set; }

    /// <summary>The period's effective monthly soft cap immediately after the reset, or null when uncapped.</summary>
    public decimal? EffectiveSoftCapAfterUsd { get; set; }

    /// <summary>The period's effective monthly hard cap immediately before the reset, or null when uncapped.</summary>
    public decimal? EffectiveHardCapBeforeUsd { get; set; }

    /// <summary>The period's effective monthly hard cap immediately after the reset, or null when uncapped.</summary>
    public decimal? EffectiveHardCapAfterUsd { get; set; }

    /// <summary>The administrator who performed the reset, or null when the acting identity could not be resolved.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>When the reset was performed (UTC).</summary>
    public DateTime PerformedAt { get; set; }
}
