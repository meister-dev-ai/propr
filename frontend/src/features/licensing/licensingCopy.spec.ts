// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  activationActionLabel,
  activationFailureMessage,
  authorPeakMonthLabel,
  capabilityReasonMessage,
  capabilityStateLabel,
  isNoticeStage,
  limitLabel,
} from './licensingCopy'
import type { PremiumCapability } from '@/services/licensingService'

function unavailableCapability(overrides: Partial<PremiumCapability> = {}): PremiumCapability {
  return {
    key: 'budgeting',
    displayName: 'Budgeting',
    requiresCommercial: true,
    overrideState: 'default',
    isAvailable: false,
    message: 'The backend wording',
    reason: 'notInLicense',
    ...overrides,
  }
}

describe('authorPeakMonthLabel', () => {
  it('leaves out a malformed calendar date instead of displaying its normalized month', () => {
    expect(authorPeakMonthLabel({ month: '2026-02-30', authorCount: 19 })).toBeNull()
  })
})

describe('forward-compatible copy', () => {
  it('uses the fallback for unknown values that collide with Object.prototype', () => {
    expect(activationFailureMessage('toString' as never, 'The backend wording')).toBe('The backend wording')
    expect(capabilityStateLabel(unavailableCapability({ reason: 'toString' }))).toBe('Not entitled')
    expect(capabilityReasonMessage(unavailableCapability({ reason: 'toString' })))
      .toBe('The backend wording')
    expect(limitLabel('toString')).toBe('toString')
    expect(activationActionLabel('toString' as never)).toBe('toString')
  })

  it('ignores an unknown lifecycle stage when deciding whether to show a notice', () => {
    expect(isNoticeStage('renewalPending')).toBe(false)
  })
})
