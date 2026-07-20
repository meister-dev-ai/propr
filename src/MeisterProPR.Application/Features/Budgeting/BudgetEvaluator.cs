// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Budgeting.Models;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Budgeting;

/// <summary>
///     Evaluates accumulated per-scope spend against a client's budget caps. Composition is most-restrictive-wins:
///     any reached cap blocks, so the tightest applicable ceiling always binds. When several caps are reached the
///     most specific scope (increment, then pull request, then client) is reported as the reason.
/// </summary>
public static class BudgetEvaluator
{
    /// <summary>
    ///     Returns the hard cap the review has reached given the effective spend in each scope, or
    ///     <see langword="null" /> when no hard cap is reached. Enforcement cuts further model calls on a non-null
    ///     result.
    /// </summary>
    public static BudgetBreach? FindHardCapBreach(
        BudgetCaps caps,
        decimal clientSpentUsd,
        decimal pullRequestSpentUsd,
        decimal incrementSpentUsd)
    {
        if (caps.IncrementHardCapUsd is { } incrementCap && incrementSpentUsd >= incrementCap)
        {
            return new BudgetBreach(BudgetScopeKind.Increment, BudgetCapKind.Hard, incrementCap, incrementSpentUsd);
        }

        if (caps.PullRequestHardCapUsd is { } pullRequestCap && pullRequestSpentUsd >= pullRequestCap)
        {
            return new BudgetBreach(BudgetScopeKind.PullRequest, BudgetCapKind.Hard, pullRequestCap, pullRequestSpentUsd);
        }

        if (caps.MonthlyHardCapUsd is { } clientCap && clientSpentUsd >= clientCap)
        {
            return new BudgetBreach(BudgetScopeKind.ClientMonthly, BudgetCapKind.Hard, clientCap, clientSpentUsd);
        }

        return null;
    }

    /// <summary>
    ///     Returns the soft cap the review has reached given the effective spend in the pull-request and client
    ///     scopes, or <see langword="null" /> when no soft cap is reached. The increment scope has no soft cap.
    /// </summary>
    public static BudgetBreach? FindSoftCapBreach(
        BudgetCaps caps,
        decimal clientSpentUsd,
        decimal pullRequestSpentUsd)
    {
        if (caps.PullRequestSoftCapUsd is { } pullRequestCap && pullRequestSpentUsd >= pullRequestCap)
        {
            return new BudgetBreach(BudgetScopeKind.PullRequest, BudgetCapKind.Soft, pullRequestCap, pullRequestSpentUsd);
        }

        if (caps.MonthlySoftCapUsd is { } clientCap && clientSpentUsd >= clientCap)
        {
            return new BudgetBreach(BudgetScopeKind.ClientMonthly, BudgetCapKind.Soft, clientCap, clientSpentUsd);
        }

        return null;
    }

    /// <summary>
    ///     Returns the cap that should keep a not-yet-started job held, or <see langword="null" /> when it may run.
    ///     A job is held when any hard cap is already reached (starting would be cut immediately) or when a soft cap
    ///     is reached (no new work is admitted). The hard-cap breach takes precedence in the reason.
    /// </summary>
    public static BudgetBreach? FindAdmissionBreach(
        BudgetCaps caps,
        decimal clientSpentUsd,
        decimal pullRequestSpentUsd,
        decimal incrementSpentUsd)
    {
        return FindHardCapBreach(caps, clientSpentUsd, pullRequestSpentUsd, incrementSpentUsd)
               ?? FindSoftCapBreach(caps, clientSpentUsd, pullRequestSpentUsd);
    }
}
