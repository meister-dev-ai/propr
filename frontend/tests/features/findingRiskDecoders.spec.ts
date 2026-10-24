// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { findingRisk } from '@/features/reviews/findingRisk'
import { decodeProtocolRiskAssessment, decodeProposedRiskAssessment } from '@/features/job-protocol/utils/findingRiskDecoders'

const api = {
  impact: 'critical', likelihood: 'likely', severity: 'critical', confidence: 'certain',
  impactReason: 'Stored records are lost.', likelihoodReason: 'Every import executes the deletion.',
  confidenceReason: 'The complete deletion path establishes the loss.',
  source: 'review-generation', rubricVersion: 'finding-risk-v1',
} as const

describe('diagnostic assessment boundaries', () => {
  it.each([
    api,
    { ...api, impact: 4, likelihood: 3, severity: 4, confidence: 4 },
    { Impact: 4, Likelihood: 3, Severity: 4, Confidence: 4, ImpactReason: api.impactReason,
      LikelihoodReason: api.likelihoodReason, ConfidenceReason: api.confidenceReason,
      Source: api.source, RubricVersion: api.rubricVersion },
  ])('normalizes a supported retained protocol shape (%j)', (value) => {
    expect(findingRisk(decodeProtocolRiskAssessment(value))).toMatchObject({
      impact: 'critical', likelihood: 'likely', riskSeverity: 'critical', confidence: 'certain',
    })
  })

  it.each([
    { ...api, Source: 'other' },
    { ...api, confidence_source: 'evidence_verification' },
    { ...api, Impact: 4 },
  ])('does not combine incompatible property shapes into trusted evidence (%j)', (value) => {
    expect(findingRisk(decodeProtocolRiskAssessment(value))).toMatchObject({ riskSeverity: 'unknown', confidence: 'unknown' })
  })

  it.each([null, undefined])('preserves missing confidence-source compatibility (%j)', (confidenceSource) => {
    expect(findingRisk(decodeProtocolRiskAssessment({ ...api, confidenceSource })).confidence).toBe('certain')
  })

  it.each(['', ' ', 42, {}, 'x'.repeat(129)])('rejects explicit invalid confidence provenance (%j)', (confidenceSource) => {
    expect(findingRisk(decodeProtocolRiskAssessment({ ...api, confidenceSource })).confidence).toBe('unknown')
  })

  it('retains independently verified confidence without assessed risk', () => {
    expect(findingRisk(decodeProtocolRiskAssessment({ ...api, source: null, confidenceSource: 'evidence_verification' })))
      .toMatchObject({ confidence: 'certain', riskSeverity: 'unknown' })
  })

  it.each([99, -1, 1.5, '4', false])('keeps malformed retained enum values unassessed (%j)', (value) => {
    expect(findingRisk(decodeProtocolRiskAssessment({ ...api, confidence: value })).confidence).toBe('unknown')
  })

  it('decodes producer rationales without accepting apparent authority', () => {
    const proposed = decodeProposedRiskAssessment({ impact: 'critical', likelihood: 'likely', confidence: 'certain',
      impact_reason: api.impactReason, likelihood_reason: api.likelihoodReason, confidence_reason: api.confidenceReason,
      severity: 'critical', source: 'evidence_verification', confidence_source: 'evidence_verification', rubric_version: api.rubricVersion })

    expect(findingRisk(proposed)).toMatchObject({ impact: 'critical', likelihood: 'likely', impactReason: api.impactReason,
      confidenceReason: api.confidenceReason, confidence: 'unknown', riskSeverity: 'unknown' })
  })

  it('accepts a camelCase producer preview without granting authority', () => {
    expect(findingRisk(decodeProposedRiskAssessment(api))).toMatchObject({ impactReason: api.impactReason, confidence: 'unknown', riskSeverity: 'unknown' })
  })

  it('does not borrow rationales from mixed producer shapes', () => {
    expect(decodeProposedRiskAssessment({ ...api, impact_reason: 'A different claim.' })).toBeNull()
  })
})
