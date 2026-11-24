// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { findingRisk } from '@/features/reviews/findingRisk'

const assessment = {
  impact: 'high', likelihood: 'possible', severity: 'high', impactReason: 'A tenant loses its stored settings.',
  likelihoodReason: 'A supported import option that is not the default reaches the branch.', source: 'review_model',
  confidence: 'medium', confidenceReason: 'The caller is visible; the import format is assumed.', confidenceSource: 'evidence_verification',
} as const

describe('finding risk rubric versions', () => {
  it.each(['finding-risk-v1', 'finding-risk-v2'])('shows the risk severity and the confidence of an assessment of %s', rubricVersion => {
    const result = findingRisk({ ...assessment, rubricVersion })

    expect(result.riskSeverity).toBe('high')
    expect(result.confidence).toBe('medium')
  })

  it.each(['finding-risk-v3', 'unsupported', null])('leaves an assessment of the rubric version %s unassessed', rubricVersion => {
    const result = findingRisk({ ...assessment, rubricVersion })

    expect(result.riskSeverity).toBe('unknown')
    expect(result.confidence).toBe('unknown')
    expect(result.impact).toBe('high')
    expect(result.likelihood).toBe('possible')
  })
})
