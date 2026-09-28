// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * The budget reason a review carries when a cap held or stopped it. A refusal relayed from another replica
 * can name the stop without naming the cap behind it, and the scope, threshold and spend are then absent.
 */
export interface BudgetBlock {
  scope?: string | null
  capKind?: string
  thresholdUsd?: number | null
  spentUsd?: number | null
}

/** Formats a budget scope for display; the raw value is used for an unrecognized scope. */
export function formatBudgetScope(scope: string | null | undefined): string {
  switch (scope) {
    case 'clientMonthly':
      return 'monthly client'
    case 'pullRequest':
      return 'per-pull-request'
    case 'increment':
      return 'per-increment'
    case 'tenantMonthly':
      return 'monthly tenant'
    default:
      return 'budget'
  }
}

/** Formats a USD amount for the budget banner; a missing amount renders as $0.00. */
export function formatBudgetUsd(value: number | null | undefined): string {
  return value == null ? '$0.00' : `$${value.toFixed(2)}`
}

/** Whether the block says a cap was reached without saying which one. */
function capIsUnknown(block: BudgetBlock): boolean {
  return block.scope == null && block.thresholdUsd == null && block.spentUsd == null
}

/**
 * Builds the operator-facing explanation for a held or budget-stopped review, or null when the job was not
 * budget-blocked. A held job never ran; a budget-stopped job kept the findings produced before the cut. Either
 * way, recovery is a manual restart.
 */
export function formatBudgetBlockMessage(
  status: string | null | undefined,
  block: BudgetBlock | null | undefined,
): string | null {
  if (status !== 'budgetHeld' && status !== 'budgetExceeded') {
    return null
  }

  const action =
    status === 'budgetHeld'
      ? 'This review was held before it started'
      : 'This review was stopped mid-run and its findings so far were kept'

  if (!block) {
    return `${action} because a budget cap was reached. Restart it after freeing budget.`
  }

  if (capIsUnknown(block)) {
    // The stop was reported by the replica that enforced the cap, and that replica holds the numbers. An
    // operator reads which cap it was on the client's budget page.
    return `${action} because a budget cap was reached; the cap details are unavailable. Restart it after freeing budget.`
  }

  const capLabel = block.capKind === 'soft' ? 'soft' : 'hard'
  return (
    `${action} because the ${formatBudgetScope(block.scope)} ${capLabel} cap of ` +
    `${formatBudgetUsd(block.thresholdUsd)} was reached (spent ${formatBudgetUsd(block.spentUsd)}). ` +
    'Restart it after freeing budget.'
  )
}

/**
 * Builds the explanation for a completed review that reached its per-increment soft cap, or null when the review
 * was not soft-capped. Unlike a held or stopped review this one finished with a synthesis over the files it did
 * review, so there is nothing to restart.
 */
export function formatBudgetSoftCapMessage(
  status: string | null | undefined,
  block: BudgetBlock | null | undefined,
): string | null {
  if (status !== 'completed' || block?.capKind !== 'soft' || capIsUnknown(block)) {
    return null
  }

  return (
    `This review reached its ${formatBudgetScope(block.scope)} soft cap of ` +
    `${formatBudgetUsd(block.thresholdUsd)} (spent ${formatBudgetUsd(block.spentUsd)}), so it stopped scanning ` +
    'further files and finished with a synthesis of what it reviewed.'
  )
}

/**
 * The explanation for a review admission refused before it started, or null when admission did not refuse
 * it. The reason is the text posted on the pull request, so the console and the pull request say the same
 * thing. The fallback covers a row that carries no reason, and names no dimension: a refusal can come from
 * the size of the pull request or from the size of the repository.
 */
export function formatAdmissionRefusalMessage(
  status: string | null | undefined,
  reason: string | null | undefined,
): string | null {
  if (status !== 'admissionRefused') {
    return null
  }

  return reason && reason.trim().length > 0
    ? reason
    : 'This review was not started because it exceeded a review limit set for this client.'
}

/**
 * The explanation for a review held because its pull request had already started its hourly number of
 * reviews, or null when it is not held. A held review starts by itself once the time has passed.
 */
export function formatAdmissionHoldMessage(
  status: string | null | undefined,
  heldUntil: string | null | undefined,
): string | null {
  if (status !== 'admissionHeld') {
    return null
  }

  const resumesAt = heldUntil ? new Date(heldUntil) : null
  const when = resumesAt && !Number.isNaN(resumesAt.valueOf()) ? resumesAt.toLocaleString() : null
  return when === null
    ? 'This review is waiting because the pull request reached its hourly review limit. It starts once the hour has passed.'
    : `This review is waiting because the pull request reached its hourly review limit. It starts by itself at ${when}.`
}
