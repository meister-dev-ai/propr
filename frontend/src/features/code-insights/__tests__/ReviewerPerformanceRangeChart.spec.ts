// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import ReviewerPerformanceRangeChart from '../components/ReviewerPerformanceRangeChart.vue'
import type { PerformanceSeries } from '@/services/reviewerPerformanceService'

const series = (date: string): PerformanceSeries => ({
  id: 'model-a',
  label: 'Model A',
  points: [
    {
      date,
      score: {
        summary: {
          f1: { minimum: 0.4, firstQuartile: 0.5, median: 0.6, thirdQuartile: 0.7, maximum: 0.8 },
        },
      },
    },
  ],
})
describe('range chart shared axes', () => {
  it('places a calendar point in the shared domain rather than stretching each panel', () => {
    const wrapper = mount(ReviewerPerformanceRangeChart, {
      props: {
        data: [series('2026-09-02')],
        metric: 'f1',
        axisDates: ['2026-09-01', '2026-09-02', '2026-09-03'],
      },
    })
    expect(Number(wrapper.get('circle').attributes('cx'))).toBeCloseTo(326.5)
    expect(wrapper.get('.chart-label-detail').text()).toContain('median 60.0%')
  })
  it('labels elapsed periods from the shared period count for disjoint calendar windows', () => {
    const wrapper = mount(ReviewerPerformanceRangeChart, {
      props: {
        data: [series('2026-09-01')],
        metric: 'f1',
        alignment: 'elapsed',
        axisPeriods: 30,
        axisDates: Array.from({ length: 60 }, (_, index) => `calendar-${index}`),
      },
    })
    expect(wrapper.findAll('.chart-date').map((tick) => tick.text())).toEqual([
      'Period 1',
      'Period 15',
      'Period 30',
    ])
  })
  it('labels the final elapsed tick correctly for a two-period window', () => {
    const wrapper = mount(ReviewerPerformanceRangeChart, {
      props: { data: [series('2026-09-01')], metric: 'f1', alignment: 'elapsed', axisPeriods: 2 },
    })
    expect(wrapper.findAll('.chart-date').map((tick) => tick.text())).toEqual([
      'Period 1',
      'Period 2',
    ])
  })
  it('keeps the calendar distance between disjoint January and September windows', () => {
    const dates = ['2026-01-01', '2026-01-02', '2026-09-01', '2026-09-02']
    const wrapper = mount(ReviewerPerformanceRangeChart, {
      props: {
        data: [
          {
            ...series(dates[0]!),
            points: [
              ...series(dates[0]!).points!,
              ...series(dates[1]!).points!,
              ...series(dates[2]!).points!,
              ...series(dates[3]!).points!,
            ],
          },
        ],
        metric: 'f1',
        axisDates: dates,
      },
    })
    const positions = wrapper.findAll('circle').map((circle) => Number(circle.attributes('cx')))
    expect(positions[1]! - positions[0]!).toBeLessThan(3)
    expect(positions[2]! - positions[1]!).toBeGreaterThan(540)
  })
})
