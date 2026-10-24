// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import FindingRiskAssessmentView from '@/components/findings/FindingRiskAssessmentView.vue'
import type { FindingRiskAssessment } from '@/features/reviews/findingRisk'
import { decodeProtocolRiskAssessment } from '@/features/job-protocol/utils/findingRiskDecoders'

const assessed = {
  impact: 'moderate', likelihood: 'unlikely', severity: 'low',
  impactReason: 'An optional report is incorrect.',
  likelihoodReason: 'Only an empty imported report takes this path.',
  source: 'review-generation', rubricVersion: 'finding-risk-v1',
} as const

describe('finding risk assessment', () => {
  it.each(['low', 'medium', 'high', 'certain'])('shows independent %s correctness confidence and evidence', (confidence) => {
    const view = mount(FindingRiskAssessmentView, { props: { assessment: {
      ...assessed, likelihood: 'notApplicable', likelihoodReason: null,
      confidence: confidence as FindingRiskAssessment['confidence'], confidenceReason: 'The deterministic reproduction follows the complete code path.',
    } } })
    expect(view.text()).toContain(`Confidence: ${confidence[0]!.toUpperCase()}${confidence.slice(1)}`)
    expect(view.text()).toContain('The deterministic reproduction follows the complete code path.')
    expect(view.text()).toContain('Risk severity: Unassessed')
  })

  it.each([
    { confidence: 99, confidenceReason: 'Evidence.' },
    { confidence: '4', confidenceReason: 'Evidence.' },
    { confidence: 'certain', confidenceReason: 'x'.repeat(1025) },
    { confidence: 'certain', confidenceReason: 'Evidence.', source: null },
    { confidence: 'certain', confidenceReason: 'Evidence.', rubricVersion: 'future' },
  ])('keeps invalid confidence Unassessed (%j)', (metadata) => {
    const view = mount(FindingRiskAssessmentView, { props: { assessment: { ...assessed, ...metadata } as FindingRiskAssessment } })
    expect(view.text()).toContain('Confidence: Unassessed')
  })

  it('shows independent axes, derived risk and readable reasons without exposing provenance', () => {
    const view = mount(FindingRiskAssessmentView, { props: { assessment: assessed } })
    expect(view.get('[aria-label="Finding risk assessment"]').text()).toContain('Risk severity: Low')
    expect(view.text()).toContain('Impact: Moderate')
    expect(view.text()).toContain('Likelihood: Unlikely')
    expect(view.text()).toContain(assessed.impactReason)
    expect(view.text()).toContain(assessed.likelihoodReason)
    expect(view.text()).not.toContain(assessed.source)
    expect(view.text()).not.toContain(assessed.rubricVersion)
  })

  it.each([null, {}, { ...assessed, impactReason: null }, { ...assessed, rubricVersion: 'future' }])(
    'renders absent or unsupported assessment as Unassessed (%j)', (assessment) => {
      const view = mount(FindingRiskAssessmentView, { props: { assessment } })
      expect(view.text()).toContain('Risk severity: Unassessed')
      expect(view.text()).not.toContain('Risk severity: Low')
    },
  )

  it('retains known impact and rationale when occurrence cannot be assessed', () => {
    const view = mount(FindingRiskAssessmentView, {
      props: { assessment: { ...assessed, likelihood: 'notApplicable', severity: 'unknown' } },
    })
    expect(view.text()).toContain('Impact: Moderate')
    expect(view.text()).toContain('Likelihood: Not applicable')
    expect(view.text()).toContain('Risk severity: Unassessed')
    expect(view.text()).toContain(assessed.impactReason)
  })

  it('does not treat model supplied severity or provenance as validated evidence', () => {
    const view = mount(FindingRiskAssessmentView, {
      props: { assessment: { ...assessed, severity: 'critical' }, authoritative: false },
    })
    expect(view.text()).toContain('Risk severity: Unassessed')
    expect(view.text()).toContain('Proposed assessment')
    expect(view.text()).not.toContain('Risk severity: Critical')
  })

  it('does not borrow legacy provenance aliases when rendering an API assessment', () => {
    const view = mount(FindingRiskAssessmentView, { props: { assessment: {
      ...assessed, source: null, Source: 'review-generation', confidence: 'certain',
      confidenceReason: 'The complete path is visible.', ConfidenceSource: 'evidence_verification',
    } as FindingRiskAssessment } })
    expect(view.text()).toContain('Risk severity: Unassessed')
    expect(view.text()).toContain('Confidence: Unassessed')
  })

  it('reads trusted serialized protocol assessments with numeric enums and Pascal properties', () => {
    const view = mount(FindingRiskAssessmentView, { props: { assessment: decodeProtocolRiskAssessment({
      Impact: 4, Likelihood: 3, Severity: 4, ImpactReason: 'The stored records can be lost.',
      LikelihoodReason: 'Every import executes this branch.', Source: 'review-generation', RubricVersion: 'finding-risk-v1',
    }) } })
    expect(view.text()).toContain('Risk severity: Critical')
    expect(view.text()).toContain('Impact: Critical')
    expect(view.text()).toContain('Likelihood: Likely')
  })
})
