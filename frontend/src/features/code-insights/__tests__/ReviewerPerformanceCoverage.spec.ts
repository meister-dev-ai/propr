// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ReviewerPerformanceEvidence from '../components/ReviewerPerformanceEvidence.vue'
import CodeInsightsMissesPanel from '../components/CodeInsightsMissesPanel.vue'
import type { CodeInsightMiss } from '@/services/codeInsightsAnalyticsService'
import type { PerformancePoint } from '@/services/reviewerPerformanceService'

describe('reviewer performance unavailable judgements', () => {
  it('shows failed human judgements as unavailable decisions without negative verdicts', () => {
    const miss: CodeInsightMiss = {
      id: 'failed',
      clientId: 'client',
      repositoryId: 'repo',
      pullRequestId: 1,
      providerThreadId: 'thread',
      discussion: 'Human concern',
      isSubstantive: false,
      wasActedOn: false,
      isInScope: false,
      countsAsMiss: false,
      classifierConfidence: null,
      harvestedAt: '2026-09-01T10:00:00Z',
      filePath: null,
      lineNumber: null,
      judgementFailed: true,
    }
    const wrapper = mount(CodeInsightsMissesPanel, { props: { misses: [miss] } })
    expect(wrapper.text()).toContain('Judgement unavailable')
    expect(wrapper.text()).toContain('Substantive: unavailable')
    expect(wrapper.text()).toContain('Acted on: unavailable')
    expect(wrapper.text()).toContain('Scope: unavailable')
    expect(wrapper.text()).not.toContain('Not substantive')
    expect(wrapper.text()).not.toContain('Not acted on')
    expect(wrapper.text()).not.toContain('Out of scope')
  })
  it('keeps unsupported recall and F1 distinct from a compatible measured no-miss population', async () => {
    const scenario = {
      id: 'first',
      truePositives: 8,
      falsePositives: 2,
      falseNegatives: 0,
      precision: 0.8,
      recall: null,
      f1: null,
    }
    const point: PerformancePoint = {
      date: '2026-09-01',
      score: {
        counts: { actedMisses: 0 },
        scenarios: [scenario],
        summary: { precision: { minimum: 0.8, maximum: 0.8 } },
      },
      unavailableReasons: ['miss-model-attribution-unavailable'],
    }
    const wrapper = mount(ReviewerPerformanceEvidence, { props: { point } })
    expect(wrapper.findAll('.performance-evidence__metrics span').map((metric) => metric.text())).toEqual([
      'Precision 80.0%–80.0%',
      'Observed recall Unavailable',
      'F1 Unavailable',
    ])
    expect(wrapper.get('.evidence-reasons').text()).toContain('human-miss model attribution')
    await wrapper.setProps({
      point: {
        ...point,
        unavailableReasons: [],
        score: {
          ...point.score,
          scenarios: [{ ...scenario, recall: 1, f1: 8 / 9 }],
          summary: {
            precision: { minimum: 0.8, maximum: 0.8 },
            recall: { minimum: 1, maximum: 1 },
            f1: { minimum: 8 / 9, maximum: 8 / 9 },
          },
        },
      },
    })
    expect(wrapper.findAll('.performance-evidence__metrics span').map((metric) => metric.text())).toEqual([
      'Precision 80.0%–80.0%',
      'Observed recall 100.0%–100.0%',
      'F1 88.9%–88.9%',
    ])
    expect(wrapper.find('.evidence-reasons').exists()).toBe(false)
  })
})
