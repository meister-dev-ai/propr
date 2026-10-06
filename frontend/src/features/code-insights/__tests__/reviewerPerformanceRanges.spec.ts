// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import {
  connectedSegments,
  stableSeriesColor,
  pairedDelta,
  annotationLayout,
} from '../reviewerPerformanceRanges'
import { mockPerformanceResponse } from '@/mocks/reviewerPerformance'

describe('retained reviewer performance presentation', () => {
  it('retains prior cumulative mock model evidence through inactive dates while period mode has gaps', () => {
    const from = new Date(Date.now() - 9 * 86400000).toISOString().slice(0, 10)
    const to = new Date(Date.now() - 2 * 86400000).toISOString().slice(0, 10)
    const query = { grouping: 'model', views: [{ from, to }] }
    const cumulative = mockPerformanceResponse(query).views?.[0]?.series?.find(
      (series) => series.label === 'review-large',
    )
    const period = mockPerformanceResponse({
      ...query,
      aggregation: 'period',
    }).views?.[0]?.series?.find((series) => series.label === 'review-large')
    expect(cumulative?.points?.at(-1)?.score?.summary?.precision).toBeDefined()
    expect(cumulative?.points?.at(-1)?.score?.counts?.outcomes?.positive).toBeGreaterThan(0)
    expect(period?.points?.at(-1)?.score?.summary?.precision).toBeUndefined()
  })
  it('keeps mock model miss verdicts unavailable while retaining supported precision', () => {
    const from = new Date(Date.now() - 9 * 86400000).toISOString().slice(0, 10)
    const to = new Date(Date.now() - 2 * 86400000).toISOString().slice(0, 10)
    const response = mockPerformanceResponse({ grouping: 'model', views: [{ from, to }] })
    const point = response.views
      ?.flatMap((view) => view.series?.flatMap((series) => series.points ?? []) ?? [])
      .find((point) => point.score?.summary?.precision)
    expect(point).toBeDefined()
    expect(
      point?.score?.scenarios?.every(
        (scenario) =>
          scenario.falseNegatives == null && scenario.recall == null && scenario.f1 == null,
      ),
    ).toBe(true)
    expect(point?.score?.scenarios?.some((scenario) => scenario.precision != null)).toBe(true)
    expect(response.views?.[0]?.evidence?.projectionVersion).toBe(3)
  })
  it('breaks the band at unavailable periods without joining or extending them', () => {
    const points = [
      { date: '2026-09-01', score: { summary: { f1: { median: 0.5 } } } },
      { date: '2026-09-02', score: { summary: { f1: null } } },
      { date: '2026-09-03', score: { summary: { f1: { median: 0.8 } } } },
    ]
    expect(
      connectedSegments(points, 'f1').map((segment) => segment.map((point) => point.date)),
    ).toEqual([['2026-09-01'], ['2026-09-03']])
  })
  it('keeps series colors independent of ordering and filters', () => {
    expect(stableSeriesColor('actual-model-a')).toBe(stableSeriesColor('actual-model-a'))
    const colors = ['a', 'b', 'c'].map((id) => [id, stableSeriesColor(id)])
    expect(['c', 'a'].map((id) => [id, stableSeriesColor(id)])).toEqual([colors[2], colors[0]])
  })
  it('compares paired premise tuples instead of subtracting independent range endpoints', () => {
    const a = [
      { id: 'low', f1: 0.2 },
      { id: 'high', f1: 0.8 },
    ]
    const b = [
      { id: 'low', f1: 0.3 },
      { id: 'high', f1: 0.9 },
    ]
    const result = pairedDelta(a, b, 'f1')
    expect(result?.minimum).toBeCloseTo(0.1)
    expect(result?.maximum).toBeCloseTo(0.1)
    expect(pairedDelta(a, [{ id: 'different', f1: 0.7 }], 'f1')).toBeNull()
  })
  it('separates nearby direct labels without changing their measured anchors', () => {
    const result = annotationLayout(
      [
        { id: 'a', anchorY: 50 },
        { id: 'b', anchorY: 51 },
        { id: 'c', anchorY: 52 },
      ],
      10,
      300,
    )
    expect(result[1]!.labelY - result[0]!.labelY).toBeGreaterThanOrEqual(34)
    expect(result[2]!.anchorY).toBe(52)
  })
})
