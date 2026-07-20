// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { formatBudgetBlockMessage } from '../budgetBlock'

describe('formatBudgetBlockMessage', () => {
  it('returns null for a job that was not budget-blocked', () => {
    expect(formatBudgetBlockMessage('completed', null)).toBeNull()
    expect(formatBudgetBlockMessage('processing', { scope: 'clientMonthly' })).toBeNull()
    expect(formatBudgetBlockMessage(null, null)).toBeNull()
  })

  it('explains a soft-cap held job with its scope, threshold, and spend', () => {
    const message = formatBudgetBlockMessage('budgetHeld', {
      scope: 'clientMonthly',
      capKind: 'soft',
      thresholdUsd: 80,
      spentUsd: 80,
    })

    expect(message).toContain('held before it started')
    expect(message).toContain('monthly client soft cap of $80.00')
    expect(message).toContain('spent $80.00')
    expect(message).toContain('Restart it after freeing budget.')
  })

  it('explains a hard-cap stopped job and notes findings were kept', () => {
    const message = formatBudgetBlockMessage('budgetExceeded', {
      scope: 'increment',
      capKind: 'hard',
      thresholdUsd: 5,
      spentUsd: 6.5,
    })

    expect(message).toContain('stopped mid-run and its findings so far were kept')
    expect(message).toContain('per-increment hard cap of $5.00')
    expect(message).toContain('spent $6.50')
  })

  it('falls back to a generic reason when no structured budget detail is present', () => {
    const message = formatBudgetBlockMessage('budgetExceeded', null)
    expect(message).toContain('a budget cap was reached')
    expect(message).toContain('Restart it after freeing budget.')
  })
})
