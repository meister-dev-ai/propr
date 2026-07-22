// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Budgeting.Models;

/// <summary>
///     The details of a budget transition to publish: which client/scope reached which cap, the threshold and
///     spend that reached it, and the job/PR/increment context. The event type is derived from the breach's cap
///     kind so callers pass the breach they already have.
/// </summary>
/// <param name="ClientId">The client whose budget was reached.</param>
/// <param name="EventType">Whether a soft or a hard cap was reached.</param>
/// <param name="Scope">The budget scope whose cap was reached.</param>
/// <param name="ThresholdUsd">The USD threshold that was reached.</param>
/// <param name="SpentUsd">The accumulated USD spend that reached the threshold.</param>
/// <param name="JobId">The review job the transition occurred for, when known.</param>
/// <param name="PullRequestId">The pull request the transition relates to, when known.</param>
/// <param name="IterationId">The review increment the transition relates to, when known.</param>
public sealed record BudgetEventNotification(
    Guid ClientId,
    BudgetEventType EventType,
    BudgetScopeKind Scope,
    decimal ThresholdUsd,
    decimal SpentUsd,
    Guid? JobId = null,
    int? PullRequestId = null,
    int? IterationId = null)
{
    /// <summary>
    ///     Builds a notification from a reached <paramref name="breach" /> and its job context. The event type is
    ///     <see cref="BudgetEventType.HardCapReached" /> for a hard cap and <see cref="BudgetEventType.SoftCapReached" />
    ///     otherwise.
    /// </summary>
    public static BudgetEventNotification FromBreach(
        BudgetBreach breach,
        Guid clientId,
        Guid jobId,
        int pullRequestId,
        int iterationId)
    {
        ArgumentNullException.ThrowIfNull(breach);

        var eventType = breach.CapKind == BudgetCapKind.Hard
            ? BudgetEventType.HardCapReached
            : BudgetEventType.SoftCapReached;

        return new BudgetEventNotification(
            clientId,
            eventType,
            breach.Scope,
            breach.ThresholdUsd,
            breach.SpentUsd,
            jobId,
            pullRequestId,
            iterationId);
    }
}
