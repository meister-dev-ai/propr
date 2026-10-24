// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { findingRisk } from '@/features/reviews/findingRisk'

describe('independent confidence evidence', () => {
  it('displays verified confidence without inventing assessed impact or likelihood', () => {
    const result = findingRisk({ rubricVersion: 'finding-risk-v1', source: null, confidenceSource: 'evidence_verification',
      confidence: 'high', confidenceReason: 'The source establishes the reachable witness.' })

    expect(result.confidence).toBe('high')
    expect(result.riskSeverity).toBe('unknown')
  })

  it('retains compatibility with historical assessments and respects nonauthoritative inputs', () => {
    const historical = { rubricVersion: 'finding-risk-v1', source: 'review_model', confidence: 'certain', confidenceReason: 'Historical evidence.' } as const
    expect(findingRisk(historical).confidence).toBe('certain')
    expect(findingRisk(historical, false).confidence).toBe('unknown')
  })
})
