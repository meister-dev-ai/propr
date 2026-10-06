// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, type VueWrapper } from '@vue/test-utils'
import type { PerformancePoint } from '@/services/reviewerPerformanceService'
import ReviewerPerformanceEvidence from '../components/ReviewerPerformanceEvidence.vue'
import { installControlTestViewport } from './performanceControlTestViewport'

const retainedPoint: PerformancePoint = {
  date: '2026-09-02',
  windowFrom: '2026-09-01',
  windowTo: '2026-09-02',
  score: {
    counts: {
      outcomes: { positive: 8, wrong: 2, total: 10 },
      confirmedDuplicates: { positive: 2, wrong: 1, total: 3 },
      actedMisses: 1,
      unactedMisses: 2,
      provisionalMisses: 3,
      generated: 12,
      suppressedRepeats: 4,
      withheld: 5,
      publicationUnknown: 6,
      duplicateChecked: 7,
      duplicateSuspected: 8,
      duplicateUnknown: 9,
      unclassified: 10,
      failedMissJudgements: 11,
    },
    summary: {
      precision: { minimum: 0.4, maximum: 0.8 },
      recall: { minimum: 0.5, maximum: 1 },
    },
    scenarios: [
      {
        id: 'first',
        dismissed: 'positive',
        wontFix: 'negative',
        byDesign: 'excluded',
        misses: 'acted',
        duplicates: 'retain',
        truePositives: 8,
        falsePositives: 2,
        falseNegatives: 1,
        excluded: 3,
        precision: 0.8,
        recall: null,
        f1: null,
      },
      { id: 'second', truePositives: 4, falsePositives: 6, falseNegatives: 2 },
    ],
  },
}

describe('reviewer performance evidence presentation', () => {
  let wrapper: VueWrapper | undefined

  beforeEach(installControlTestViewport)

  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
    document.body.innerHTML = ''
    vi.unstubAllGlobals()
  })

  it('renders the selected window and metric ranges without a count or premise inspector', () => {
    wrapper = mount(ReviewerPerformanceEvidence, { props: { point: retainedPoint } })

    expect(wrapper.get('header strong').text()).toBe('2026-09-01 → 2026-09-02')
    expect(
      wrapper.findAll('.performance-evidence__metrics span').map((metric) => metric.text()),
    ).toEqual(['Precision 40.0%–80.0%', 'Observed recall 50.0%–100.0%', 'F1 Unavailable'])
    expect(wrapper.find('details').exists()).toBe(false)
    expect(wrapper.find('dl').exists()).toBe(false)
    expect(wrapper.find('[role="combobox"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('versioned premises')
    expect(wrapper.text()).not.toContain('TP 8')
  })

  it('updates the displayed window, ranges and limitations when another point is selected', async () => {
    wrapper = mount(ReviewerPerformanceEvidence, { props: { point: retainedPoint } })
    await wrapper.setProps({
      point: {
        date: '2026-09-03',
        score: {
          summary: {
            precision: { minimum: 0.6, maximum: 0.9 },
            f1: { minimum: 0.7, maximum: 0.85 },
          },
        },
        unavailableReasons: ['duplicate-verification-incomplete'],
      },
    })

    expect(wrapper.get('header strong').text()).toBe('2026-09-03 → 2026-09-03')
    expect(
      wrapper.findAll('.performance-evidence__metrics span').map((metric) => metric.text()),
    ).toEqual(['Precision 60.0%–90.0%', 'Observed recall Unavailable', 'F1 70.0%–85.0%'])
    expect(wrapper.findAll('.evidence-reasons li').map((reason) => reason.text())).toEqual([
      'Unknown or suspected duplication retains the recorded outcome contribution. Verification coverage is incomplete.',
    ])
  })

  it('retains provisional and unavailable explanations without introducing a scenario selector', () => {
    wrapper = mount(ReviewerPerformanceEvidence, {
      props: {
        point: {
          ...retainedPoint,
          unavailableReasons: ['miss-observation-provisional', 'provider-specific-coverage'],
        },
      },
    })

    expect(wrapper.findAll('.evidence-reasons li').map((reason) => reason.text())).toEqual([
      'Open human threads are provisional and excluded from the settled-miss premises.',
      'provider-specific-coverage',
    ])
    expect(wrapper.find('input').exists()).toBe(false)
    expect(wrapper.find('details').exists()).toBe(false)
  })

  it('removes limitations when the selected point has no coverage warnings', async () => {
    wrapper = mount(ReviewerPerformanceEvidence, {
      props: { point: { ...retainedPoint, unavailableReasons: ['miss-judgement-failed'] } },
    })
    expect(wrapper.find('.evidence-reasons').exists()).toBe(true)

    await wrapper.setProps({ point: retainedPoint })
    expect(wrapper.find('.evidence-reasons').exists()).toBe(false)
  })

  it('removes evidence when no point is selected', async () => {
    wrapper = mount(ReviewerPerformanceEvidence, { props: { point: retainedPoint } })
    await wrapper.setProps({ point: undefined })
    expect(wrapper.find('.performance-evidence').exists()).toBe(false)
  })
})
