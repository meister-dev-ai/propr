// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>The result of attempting a manual spend reset on a client's current budget period.</summary>
public enum BudgetSpendResetOutcome
{
    /// <summary>The reset was recorded and the period's effective cap was raised.</summary>
    Applied = 0,

    /// <summary>No such client exists, so nothing was recorded.</summary>
    ClientNotFound = 1,

    /// <summary>
    ///     The client has no monthly cap configured, so there is no ceiling to raise. Topping up an uncapped client
    ///     would silently start enforcing one that opted out.
    /// </summary>
    NoMonthlyCapConfigured = 2,
}
