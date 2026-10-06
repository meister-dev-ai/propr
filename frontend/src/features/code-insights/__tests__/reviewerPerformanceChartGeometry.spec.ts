// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import type { PerformanceSeries } from '@/services/reviewerPerformanceService'
import { boundedTooltipPosition, chartGeometry } from '../reviewerPerformanceChartGeometry'
import { inspectionEntries, navigateInspection, nearestInspection } from '../reviewerPerformanceChartInspection'

const data: PerformanceSeries[] = [
  {
    id: 'january',
    label: 'January cohort',
    points: [
      { date: '2026-01-01', score: { summary: { f1: { median: 0.6 } } } },
      { date: '2026-01-02', unavailableReasons: ['no-series-observation-in-period'] },
      { date: '2026-01-03', score: { summary: { f1: { median: 0.8 } } } },
    ],
  },
  {
    id: 'september',
    label: 'September cohort',
    points: [
      { date: '2026-09-01', score: { summary: { f1: { median: 0.7 } } } },
      { date: '2026-09-02', score: { summary: { f1: { median: 0.9 } } } },
    ],
  },
]

describe('chart inspection coordinates and navigation', () => {
  it('uses actual calendar distances across the shared domain', () => {
    const geometry = chartGeometry({
      data,
      height: 360,
      alignment: 'calendar',
      axisDates: ['2026-01-01', '2026-09-02'],
      axisPeriods: 0,
    })
    const january = geometry.x(data[0]!, data[0]!.points![1]!)
    const september = geometry.x(data[1]!, data[1]!.points![0]!)
    expect(january).toBeGreaterThan(48)
    expect(january).toBeLessThan(51)
    expect(september - january).toBeGreaterThan(550)
    expect(geometry.x(data[1]!, data[1]!.points![1]!)).toBe(605)
  })

  it('uses the selected series ordinal on shared elapsed axes and leaves missing dates undefined', () => {
    const geometry = chartGeometry({
      data,
      height: 360,
      alignment: 'elapsed',
      axisDates: [],
      axisPeriods: 5,
    })
    expect(geometry.x(data[0]!, data[0]!.points![1]!)).toBe(187.25)
    expect(geometry.x(data[1]!, data[1]!.points![1]!)).toBe(187.25)
    expect(geometry.selectedX('2026-09-02', 'september')).toBe(187.25)
    expect(geometry.selectedX('2026-09-03', 'september')).toBeUndefined()
  })

  it('retains unavailable periods for pointer and bounded keyboard navigation', () => {
    const geometry = chartGeometry({
      data,
      height: 360,
      alignment: 'elapsed',
      axisDates: [],
      axisPeriods: 5,
    })
    const entries = inspectionEntries(data, 'f1', geometry, 360)
    const first = entries[0]!
    const missing = entries[1]!
    expect(missing.unavailable).toBe(true)
    expect(missing.point).toBe(data[0]!.points![1])
    expect(nearestInspection(entries, { x: missing.x, y: missing.y })).toBe(missing)
    expect(navigateInspection(entries, first, 'ArrowLeft')).toBe(first)
    expect(navigateInspection(entries, first, 'ArrowRight')).toBe(missing)
    expect(navigateInspection(entries, missing, 'ArrowDown').date).toBe('2026-09-02')
    const last = navigateInspection(entries, first, 'End')
    expect(last.date).toBe('2026-01-03')
    expect(navigateInspection(entries, last, 'ArrowRight')).toBe(last)
    expect(nearestInspection([], { x: 0, y: 0 })).toBeUndefined()
  })

  it('preserves the elapsed period when series share a date at different ordinals', () => {
    const cohorts: PerformanceSeries[] = [
      { id: 'first', points: [{ date: '2026-01-01' }, { date: '2026-01-02' }] },
      { id: 'second', points: [{ date: '2026-01-02' }, { date: '2026-01-03' }] },
    ]
    const geometry = chartGeometry({ data: cohorts, height: 360, alignment: 'elapsed', axisDates: [], axisPeriods: 2 })
    const entries = inspectionEntries(cohorts, 'f1', geometry, 360)
    const destination = navigateInspection(entries, entries[1]!, 'ArrowDown')

    expect(destination.seriesId).toBe('second')
    expect(destination.date).toBe('2026-01-03')
    expect(destination.pointIndex).toBe(1)
    expect(destination.x).toBe(entries[1]!.x)
  })

  it.each([
    {
      anchor: { x: 385, y: 790 },
      size: { width: 360, height: 200 },
      viewport: { width: 390, height: 844 },
    },
    {
      anchor: { x: 5, y: 10 },
      size: { width: 304, height: 164 },
      viewport: { width: 320, height: 180 },
    },
  ])('keeps tooltip bounds inside the viewport for $viewport.width pixels', ({ anchor, size, viewport }) => {
    const position = boundedTooltipPosition(anchor, size, viewport)
    expect(position.x).toBeGreaterThanOrEqual(8)
    expect(position.y).toBeGreaterThanOrEqual(8)
    expect(position.x + size.width).toBeLessThanOrEqual(viewport.width - 8)
    expect(position.y + size.height).toBeLessThanOrEqual(viewport.height - 8)
  })
})
