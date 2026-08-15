// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Status of a mention reply job.
/// </summary>
public enum MentionJobStatus
{
    /// <summary>Job is queued and waiting to start.</summary>
    Pending,

    /// <summary>Job is currently processing.</summary>
    Processing,

    /// <summary>Job completed successfully.</summary>
    Completed,

    /// <summary>Job failed with an error.</summary>
    Failed,

    /// <summary>
    ///     A budget cap stopped the answer, either because one was already reached when the mention came due
    ///     or because the answer's own call reached one. Terminal: the developer was told the budget is
    ///     exhausted, so re-running the job would answer a question that has already had its reply.
    /// </summary>
    BudgetHeld,
}
