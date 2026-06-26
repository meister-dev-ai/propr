// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { formatTriageDecision } from './formatTriageDecision'

describe('formatTriageDecision', () => {
  it('formats a measured, security-flagged decision', () => {
    const r = formatTriageDecision({
      tier: 'Medium',
      why: 'touches auth',
      securityFlagged: true,
      fanOutKind: 'Measured',
      fanOutCount: 5,
    })

    expect(r.tier).toBe('Medium')
    expect(r.why).toBe('touches auth')
    expect(r.security).toBe('Security-flagged')
    expect(r.blastRadius).toBe('5 callers')
  })

  it('treats Unavailable fan-out as "no data", never "0 callers" (absence != zero)', () => {
    const r = formatTriageDecision({ tier: 'Low', fanOutKind: 'Unavailable', fanOutCount: null })

    expect(r.blastRadius).toBe('no data')
  })

  it('distinguishes a measured zero from no data', () => {
    expect(formatTriageDecision({ fanOutKind: 'Measured', fanOutCount: 0 }).blastRadius).toBe('0 callers')
    expect(formatTriageDecision({ fanOutKind: 'Measured', fanOutCount: 1 }).blastRadius).toBe('1 caller')
  })

  it('marks Truncated as many (high)', () => {
    expect(formatTriageDecision({ fanOutKind: 'Truncated', fanOutCount: 50 }).blastRadius).toBe('many callers (truncated)')
  })

  it('falls back gracefully on missing fields', () => {
    const r = formatTriageDecision({})

    expect(r.tier).toBe('Unknown')
    expect(r.why).toBe('—')
    expect(r.security).toBe('Not flagged')
    expect(r.blastRadius).toBe('no data')
  })
})
