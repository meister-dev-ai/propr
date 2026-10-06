// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { PerformancePoint, PerformanceSeries } from '@/services/reviewerPerformanceService'

// The remaining SVG width contains the direct series annotations.
export const CHART_WIDTH = 940
export const PLOT_LEFT = 48
export const PLOT_RIGHT = 605
export const PLOT_TOP = 18
export const PLOT_BOTTOM_MARGIN = 52
export const UNAVAILABLE_BOTTOM_MARGIN = 43
const PLOT_WIDTH = PLOT_RIGHT - PLOT_LEFT
const DAY_MILLISECONDS = 86400000
const TOOLTIP_MARGIN = 8
const TOOLTIP_GAP = 12

export interface ChartDateTick {
  key: string
  x: number
  label: string
}

export interface ChartGeometryOptions {
  data: PerformanceSeries[]
  height: number
  alignment: 'calendar' | 'elapsed'
  axisDates: string[]
  axisPeriods: number
}

export interface ChartGeometry {
  x: (series: PerformanceSeries, point: PerformancePoint) => number
  y: (value: number) => number
  selectedX: (date: string, seriesId?: string) => number | undefined
  ticks: ChartDateTick[]
}

function cohortDay(date: string): number {
  return Date.parse(`${date}T00:00:00Z`) / DAY_MILLISECONDS
}

function calendarDates(options: ChartGeometryOptions): string[] {
  if (options.axisDates.length) {
    return options.axisDates
  }
  const observed = new Set<string>()
  for (const series of options.data) {
    for (const point of series.points ?? []) {
      if (point.date) {
        observed.add(point.date)
      }
    }
  }
  return [...observed].sort()
}

export function chartGeometry(options: ChartGeometryOptions): ChartGeometry {
  const dates = calendarDates(options)
  const periods = options.axisPeriods || Math.max(1, ...options.data.map((series) => series.points?.length ?? 0))
  const from = cohortDay(dates[0] ?? '1970-01-01')
  const span = Math.max(1, cohortDay(dates.at(-1) ?? '1970-01-01') - from)
  const plotHeight = options.height - PLOT_BOTTOM_MARGIN - PLOT_TOP

  function periodX(index: number): number {
    return PLOT_LEFT + (index * PLOT_WIDTH) / Math.max(1, periods - 1)
  }

  function calendarX(date: string): number {
    const elapsedDays = Math.max(0, cohortDay(date) - from)
    return PLOT_LEFT + (elapsedDays * PLOT_WIDTH) / span
  }

  function pointX(series: PerformanceSeries, point: PerformancePoint): number {
    if (options.alignment === 'calendar') {
      return calendarX(point.date ?? dates[0] ?? '1970-01-01')
    }
    const index = (series.points ?? []).findIndex((item) => item.date === point.date)
    return periodX(Math.max(0, index))
  }

  function selectedX(date: string, seriesId?: string): number | undefined {
    if (options.alignment === 'calendar') {
      return calendarX(date)
    }
    const series = options.data.find((item) => item.id === seriesId) ?? options.data[0]
    const index = (series?.points ?? []).findIndex((point) => point.date === date)
    if (index < 0) {
      return undefined
    }
    return periodX(index)
  }

  const ticks: ChartDateTick[] = []
  if (options.alignment === 'calendar' && dates.length) {
    const offsets = [...new Set([0, Math.floor(span / 2), span])]
    for (const offset of offsets) {
      ticks.push({
        key: `calendar/${offset}`,
        x: PLOT_LEFT + (offset * PLOT_WIDTH) / span,
        label: new Date((from + offset) * DAY_MILLISECONDS).toISOString().slice(0, 10),
      })
    }
  } else if (options.alignment === 'elapsed') {
    const indices = [...new Set([0, Math.floor((periods - 1) / 2), periods - 1])]
    for (const index of indices) {
      ticks.push({
        key: `elapsed/${index}`,
        x: periodX(index),
        label: `Period ${index + 1}`,
      })
    }
  }

  return {
    x: pointX,
    y: (value) => PLOT_TOP + (1 - value) * plotHeight,
    selectedX,
    ticks,
  }
}

export interface ScreenPoint {
  x: number
  y: number
}

export interface ScreenSize {
  width: number
  height: number
}

export function boundedTooltipPosition(
  anchor: ScreenPoint,
  size: ScreenSize,
  viewport: ScreenSize,
): ScreenPoint {
  const maximumLeft = viewport.width - size.width - TOOLTIP_MARGIN
  const above = anchor.y - size.height - TOOLTIP_GAP
  let top = anchor.y + TOOLTIP_GAP
  if (above >= TOOLTIP_MARGIN) {
    top = above
  }
  const maximumTop = viewport.height - size.height - TOOLTIP_MARGIN
  return {
    x: Math.max(TOOLTIP_MARGIN, Math.min(anchor.x + TOOLTIP_GAP, maximumLeft)),
    y: Math.max(TOOLTIP_MARGIN, Math.min(top, maximumTop)),
  }
}
