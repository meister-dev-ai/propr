// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ReviewerPerformanceScopeFilters from '../components/ReviewerPerformanceScopeFilters.vue'
import ReviewerPerformanceFacet from '../components/ReviewerPerformanceFacet.vue'
import { EXPLORER_DIMENSIONS } from '../reviewerPerformanceExplorer'
import { prepareEvidence } from '../reviewerPerformanceEvidence'

describe('risk dimensions in reviewer performance', () => {
  it('preserves exact facet identities and the difference between All and None', () => {
    const wrapper = mount(ReviewerPerformanceScopeFilters, { props: {
      scope: { from: '2026-10-01', to: '2026-10-20' },
      facets: { riskSeverities: [{ id: 'Unknown', label: 'Unknown' }, { id: 'Critical', label: 'Critical' }] },
    } })
    for (const dimension of ['impacts', 'likelihoods', 'riskSeverities', 'confidences']) {
      const facet = wrapper.findAllComponents(ReviewerPerformanceFacet).find(item => item.attributes('data-test') === `scope-${dimension}`)!
      expect(facet.props('selection')).toBeNull()
      facet.vm.$emit('update:selection', [])
      expect(wrapper.emitted('update:scope')?.at(-1)?.[0]).toMatchObject({ [dimension]: [] })
      facet.vm.$emit('update:selection', null)
      expect(wrapper.emitted('update:scope')?.at(-1)?.[0]).toMatchObject({ [dimension]: null })
    }
    const risk = wrapper.findAllComponents(ReviewerPerformanceFacet).find(facet => facet.props('label') === 'Risk severity')!
    expect(risk.props('items')).toContainEqual({ id: 'Unknown', label: 'Unassessed' })
    risk.vm.$emit('update:selection', ['Critical', 'Unknown'])
    expect(wrapper.emitted('update:scope')?.at(-1)?.[0]).toMatchObject({ riskSeverities: ['Critical', 'Unknown'] })
  })

  it('offers risk severity, impact and likelihood as independent matrix axes', () => {
    expect(EXPLORER_DIMENSIONS.map(dimension => dimension.id)).toEqual(expect.arrayContaining(['impact', 'likelihood', 'riskSeverity', 'confidence']))
  })

  it('explains unsupported risk recall while retaining independently measured precision', () => {
    const evidence = prepareEvidence({ date: '2026-10-20',
      overallPrecision: { summary: { minimum: 0.5, maximum: 0.9 } },
      unavailableReasons: ['miss-risk-assessment-unavailable', 'sealed-risk-filter-unavailable'],
    })!
    expect(evidence.metrics[0].range).toBe('50.0%–90.0%')
    expect(evidence.metrics[1].range).toBe('Unavailable')
    expect(evidence.unavailableReasons.map(reason => reason.label).join(' ')).toContain('independent human')
    expect(evidence.unavailableReasons.map(reason => reason.label).join(' ')).toContain('whole pull request')
  })
})
