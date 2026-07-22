// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     A persisted record of a budget cap being reached during review activity. Each meaningful transition
///     (a soft-cap admission hold, an increment in-run stop, or a hard-cap cut) writes one row. The rows are the
///     queryable contract a notification/alerting capability consumes; budgeting itself never delivers them.
/// </summary>
public sealed class BudgetEvent
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>The client whose budget was reached.</summary>
    public Guid ClientId { get; set; }

    /// <summary>Whether a soft or a hard cap was reached.</summary>
    public BudgetEventType EventType { get; set; }

    /// <summary>The budget scope whose cap was reached.</summary>
    public BudgetScopeKind Scope { get; set; }

    /// <summary>The USD threshold that was reached.</summary>
    public decimal ThresholdUsd { get; set; }

    /// <summary>The accumulated USD spend that reached the threshold.</summary>
    public decimal SpentUsd { get; set; }

    /// <summary>The review job the transition occurred for, when the event has a job context.</summary>
    public Guid? JobId { get; set; }

    /// <summary>The pull request the transition relates to, when known.</summary>
    public int? PullRequestId { get; set; }

    /// <summary>The review increment (iteration) the transition relates to, when known.</summary>
    public int? IterationId { get; set; }

    /// <summary>When the transition occurred (UTC).</summary>
    public DateTime OccurredAt { get; set; }
}
