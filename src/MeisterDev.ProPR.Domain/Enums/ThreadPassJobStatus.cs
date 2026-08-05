// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Lifecycle of one thread pass over a pull request's reviewer-owned comment threads.
/// </summary>
public enum ThreadPassJobStatus
{
    /// <summary>Queued and waiting to be claimed.</summary>
    Pending = 0,

    /// <summary>Claimed by an executor.</summary>
    Processing = 1,

    /// <summary>Every thread the pass found was handled or deliberately skipped.</summary>
    Completed = 2,

    /// <summary>Every permitted attempt failed. Terminal, and reached explicitly rather than by deleting the row.</summary>
    Failed = 3,

    /// <summary>The pull request stopped being active before the pass could finish.</summary>
    Cancelled = 4,

    /// <summary>
    ///     A budget cap was already reached when the pass was due, so it never started. Recovery is a manual
    ///     restart after budget is freed, never an automatic resume.
    /// </summary>
    BudgetHeld = 5,

    /// <summary>
    ///     A hard budget cap was reached part-way through, so the pass stopped. Threads it had already dealt
    ///     with keep their progress; the rest are left for a later pass.
    /// </summary>
    BudgetExceeded = 6,

    /// <summary>
    ///     Terminal, and reached having touched nothing: the client's gates were shut, the provider was
    ///     deactivated, the pull request was not active, or the revision had already moved on. A pass in this
    ///     status blocks no future pass, so the identical trigger runs again once the reason is gone. Every
    ///     other terminal status blocks its trigger, which is what stops a deterministic failure looping.
    /// </summary>
    Skipped = 7,
}
