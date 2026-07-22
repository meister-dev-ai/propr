// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>The kind of budget transition a <see cref="Entities.BudgetEvent" /> records.</summary>
public enum BudgetEventType
{
    /// <summary>A soft cap was reached: new work is held (client/PR admission) or a running job stops scanning (increment).</summary>
    SoftCapReached = 0,

    /// <summary>A hard cap was reached: a new review is held at admission, or a running review is stopped at the next model-call boundary.</summary>
    HardCapReached = 1,
}
