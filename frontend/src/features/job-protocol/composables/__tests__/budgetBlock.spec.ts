// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  formatAdmissionHoldMessage,
  formatAdmissionRefusalMessage,
  formatBudgetBlockMessage,
  formatBudgetSoftCapMessage,
} from '../budgetBlock'

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

  it('names the tenant scope on a job held by the tenant cap', () => {
    const message = formatBudgetBlockMessage('budgetHeld', {
      scope: 'tenantMonthly',
      capKind: 'hard',
      thresholdUsd: 5000,
      spentUsd: 5000,
    })

    expect(message).toContain('monthly tenant hard cap of $5000.00')
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

  it('says the cap details are unavailable when the stop named no cap', () => {
    const message = formatBudgetBlockMessage('budgetExceeded', { capKind: 'hard' })

    expect(message).toContain('stopped mid-run and its findings so far were kept')
    expect(message).toContain('the cap details are unavailable')
    expect(message).toContain('Restart it after freeing budget.')
    expect(message).not.toContain('$0.00')
  })

  it('does not treat a completed soft-capped job as budget-blocked', () => {
    expect(
      formatBudgetBlockMessage('completed', { scope: 'increment', capKind: 'soft', thresholdUsd: 5, spentUsd: 6 }),
    ).toBeNull()
  })
})

describe('formatBudgetSoftCapMessage', () => {
  it('explains a completed review that reached its per-increment soft cap', () => {
    const message = formatBudgetSoftCapMessage('completed', {
      scope: 'increment',
      capKind: 'soft',
      thresholdUsd: 5,
      spentUsd: 6.5,
    })

    expect(message).toContain('per-increment soft cap of $5.00')
    expect(message).toContain('spent $6.50')
    expect(message).toContain('stopped scanning')
    expect(message).toContain('synthesis')
  })

  it('returns null for a soft cap whose scope and numbers are unknown', () => {
    expect(formatBudgetSoftCapMessage('completed', { capKind: 'soft' })).toBeNull()
  })

  it('returns null for a non-completed job or a hard-cap block', () => {
    expect(formatBudgetSoftCapMessage('budgetExceeded', { capKind: 'hard' })).toBeNull()
    expect(formatBudgetSoftCapMessage('completed', { capKind: 'hard' })).toBeNull()
    expect(formatBudgetSoftCapMessage('completed', null)).toBeNull()
  })
})

describe('formatAdmissionRefusalMessage', () => {
  it('returns null for a job review admission did not refuse', () => {
    expect(formatAdmissionRefusalMessage('completed', 'anything')).toBeNull()
    expect(formatAdmissionRefusalMessage('budgetHeld', 'anything')).toBeNull()
  })

  it('shows the stored reason, which is the text posted on the pull request', () => {
    const message = formatAdmissionRefusalMessage(
      'admissionRefused',
      'Review not started: 312 changed files exceed the limit of 150. Split the pull request or raise the limit.',
    )

    expect(message).toContain('312 changed files')
    expect(message).toContain('Split the pull request')
  })

  it('explains the refusal even when no reason was stored', () => {
    expect(formatAdmissionRefusalMessage('admissionRefused', null)).toContain('review limit')
  })
})

describe('formatAdmissionHoldMessage', () => {
  it('returns null for a job that is not held', () => {
    expect(formatAdmissionHoldMessage('pending', '2026-09-20T11:00:00Z')).toBeNull()
  })

  it('names when a held review starts by itself', () => {
    const message = formatAdmissionHoldMessage('admissionHeld', '2026-09-20T11:00:00Z')

    expect(message).toContain('hourly review limit')
    expect(message).toContain('starts by itself')
  })

  it('still explains the wait when no time is known', () => {
    const message = formatAdmissionHoldMessage('admissionHeld', null)

    expect(message).toContain('hourly review limit')
  })
})
