// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/** The budget reason a review carries when a cap held or stopped it. */
export interface BudgetBlock {
  scope?: string
  capKind?: string
  thresholdUsd?: number
  spentUsd?: number
}

/** Formats a budget scope for display; the raw value is used for an unrecognized scope. */
export function formatBudgetScope(scope: string | undefined): string {
  switch (scope) {
    case 'clientMonthly':
      return 'monthly client'
    case 'pullRequest':
      return 'per-pull-request'
    case 'increment':
      return 'per-increment'
    default:
      return 'budget'
  }
}

/** Formats a USD amount for the budget banner; a missing amount renders as $0.00. */
export function formatBudgetUsd(value: number | undefined): string {
  return value == null ? '$0.00' : `$${value.toFixed(2)}`
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

  const capLabel = block.capKind === 'soft' ? 'soft' : 'hard'
  return (
    `${action} because the ${formatBudgetScope(block.scope)} ${capLabel} cap of ` +
    `${formatBudgetUsd(block.thresholdUsd)} was reached (spent ${formatBudgetUsd(block.spentUsd)}). ` +
    'Restart it after freeing budget.'
  )
}
