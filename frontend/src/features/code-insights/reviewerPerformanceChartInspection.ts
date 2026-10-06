// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { PerformancePoint, PerformanceSeries } from '@/services/reviewerPerformanceService'
import { UNAVAILABLE_BOTTOM_MARGIN, type ChartGeometry, type ScreenPoint } from './reviewerPerformanceChartGeometry'
import type { PerformanceMetric } from './reviewerPerformanceRanges'

export interface ChartPointSelection {
  seriesId: string
  date: string
}

export interface ChartInspectionEntry extends ChartPointSelection, ScreenPoint {
  key: string
  seriesIndex: number
  pointIndex: number
  seriesLabel: string
  point: PerformancePoint
  unavailable: boolean
}

export function inspectionEntries(
  data: PerformanceSeries[],
  metric: PerformanceMetric,
  geometry: ChartGeometry,
  height: number,
): ChartInspectionEntry[] {
  const entries: ChartInspectionEntry[] = []
  for (const [seriesIndex, series] of data.entries()) {
    for (const [pointIndex, point] of (series.points ?? []).entries()) {
      if (!point.date) {
        continue
      }
      const median = point.score?.summary?.[metric]?.median
      const unavailable = median == null
      entries.push({
        key: `${seriesIndex}/${pointIndex}`,
        seriesId: series.id ?? '',
        seriesIndex,
        pointIndex,
        seriesLabel: series.label ?? 'All selected evidence',
        date: point.date,
        point,
        x: geometry.x(series, point),
        y: unavailable ? height - UNAVAILABLE_BOTTOM_MARGIN : geometry.y(median),
        unavailable,
      })
    }
  }
  return entries
}

export function nearestInspection(
  entries: ChartInspectionEntry[],
  position: ScreenPoint,
): ChartInspectionEntry | undefined {
  let nearest: ChartInspectionEntry | undefined
  let minimumDistance = Infinity
  for (const item of entries) {
    const distance = Math.hypot(item.x - position.x, item.y - position.y)
    if (distance < minimumDistance) {
      nearest = item
      minimumDistance = distance
    }
  }
  return nearest
}

export function navigateInspection(
  entries: ChartInspectionEntry[],
  current: ChartInspectionEntry,
  key: string,
): ChartInspectionEntry {
  const series = entries.filter((item) => item.seriesIndex === current.seriesIndex)
  if (key === 'Home') {
    return series[0] ?? current
  }
  if (key === 'End') {
    return series.at(-1) ?? current
  }
  if (key === 'ArrowLeft' || key === 'ArrowRight') {
    const index = series.findIndex((item) => item.key === current.key)
    const direction = key === 'ArrowLeft' ? -1 : 1
    const destination = Math.min(series.length - 1, Math.max(0, index + direction))
    return series[destination] ?? current
  }

  const availableSeries = [...new Set(entries.map((item) => item.seriesIndex))]
  const direction = key === 'ArrowUp' ? -1 : 1
  const index = availableSeries.indexOf(current.seriesIndex)
  const destination = Math.min(availableSeries.length - 1, Math.max(0, index + direction))
  const candidates = entries.filter((item) => item.seriesIndex === availableSeries[destination])
  // Shared x positions represent dates in calendar mode and period ordinals in elapsed mode.
  let nearest = current
  let minimumDistance = Infinity
  for (const item of candidates) {
    const distance = Math.abs(item.x - current.x)
    if (distance < minimumDistance) {
      nearest = item
      minimumDistance = distance
    }
  }
  return nearest
}
