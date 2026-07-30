// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Chart shapes for the Code Insights views, built on the same `chart.js` idiom the usage dashboard uses.
 *
 * Two conventions worth stating. Ratio charts are pinned to 0–1 so a period cannot look dramatic merely
 * because its own range is narrow. And a bucket whose metric is undefined is emitted as `null`, not `0`:
 * chart.js draws a gap for null, which is the truth, where a zero would draw a cliff that never happened.
 */

import { REVIEW_PALETTE } from '@/components/usageDashboardFormatters'
import type {
  CodeInsightMetric,
  CodeInsightMetricPoint,
  CodeInsightTypeSeries,
} from '@/services/codeInsightsAnalyticsService'

export interface ChartDataset {
  label: string
  data: (number | null)[]
  backgroundColor: string
  borderColor: string
  fill?: boolean
  tension?: number
  spanGaps?: boolean
  pointRadius?: number
  pointHoverRadius?: number
}

export interface ChartData {
  labels: string[]
  datasets: ChartDataset[]
}

/** Turns a percentage into something readable, or an em dash when the ratio is undefined. */
export function formatRatio(value: number | null | undefined): string {
  return value == null ? '—' : `${(value * 100).toFixed(1)}%`
}

/**
 * A series per core type over the window's buckets.
 *
 * The same numbers serve two shapes, because they answer different questions. Stacked bars answer "how much, and
 * of what": the total height is the volume, and the bands are the mix. Lines answer "which type is moving": a
 * single type is followable across periods, which a band in a stack is not.
 */
export function buildTypeChartData(series: CodeInsightTypeSeries, kind: 'bar' | 'line' = 'bar'): ChartData {
  const labels = Array.from(new Set(series.points.map((point) => point.bucketStart))).sort()
  const keys = series.keys.length > 0
    ? series.keys
    : Array.from(new Set(series.points.map((point) => point.key))).sort()

  const byKey = new Map<string, Map<string, number>>()
  for (const point of series.points) {
    if (!byKey.has(point.key)) byKey.set(point.key, new Map())
    const buckets = byKey.get(point.key)!
    // Several rows can share a key and bucket once a coarser bucket size merges days, so counts accumulate.
    buckets.set(point.bucketStart, (buckets.get(point.bucketStart) ?? 0) + point.count)
  }

  return {
    labels,
    datasets: keys.map((key, index) => {
      const color = REVIEW_PALETTE[index % REVIEW_PALETTE.length]
      const buckets = byKey.get(key)
      return {
        label: key || 'untyped',
        // Zero is the right value here: a type with no findings in a bucket genuinely had none.
        data: labels.map((label) => buckets?.get(label) ?? 0),
        backgroundColor: kind === 'line' ? `${color}22` : color,
        borderColor: color,
        ...(kind === 'line'
          // Unfilled, because several filled areas over one another are unreadable: the point of the line shape
          // is following one type, not judging total volume, which the stack already does better.
          ? { fill: false, tension: 0.3, pointRadius: 3, pointHoverRadius: 6, spanGaps: true }
          : {}),
      }
    }),
  }
}

/**
 * A single ratio series over the window's buckets, with gaps where the ratio is undefined.
 *
 * `minimumSample` suppresses a bucket that does not rest on enough evidence, per bucket rather than only for
 * the window total: a week holding two closed pull requests must not contribute a point to a trend line, or
 * the line says something the data cannot support at exactly the place an operator is most likely to read it.
 */
export function buildMetricChartData(
  points: CodeInsightMetricPoint[],
  select: (metric: CodeInsightMetric) => number | null,
  label: string,
  colorIndex = 0,
  minimumSample = 0,
): ChartData {
  const ordered = [...points].sort((left, right) => left.bucketStart.localeCompare(right.bucketStart))
  const color = REVIEW_PALETTE[colorIndex % REVIEW_PALETTE.length]

  return {
    labels: ordered.map((point) => point.bucketStart),
    datasets: [
      {
        label,
        data: ordered.map((point) =>
          point.metric.sampleSize < minimumSample ? null : select(point.metric),
        ),
        backgroundColor: `${color}22`,
        borderColor: color,
        fill: true,
        tension: 0.3,
        // Gaps stay gaps: a bucket with nothing to divide by is not a bucket at zero.
        spanGaps: false,
        pointRadius: 3,
        pointHoverRadius: 6,
      },
    ],
  }
}

/** Options for the unstacked per-type line chart: same axis meaning, one line per type. */
export function createTypeLineOptions() {
  return {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'top' as const },
      title: { display: false },
    },
    scales: {
      x: { grid: { display: false } },
      y: {
        beginAtZero: true,
        grid: { color: 'rgba(148, 163, 184, 0.14)' },
        title: { display: true, text: 'Findings' },
      },
    },
    interaction: { intersect: false, mode: 'index' as const },
  }
}

/** Options for the stacked type chart. */
export function createStackedOptions() {
  return {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'top' as const },
      title: { display: false },
    },
    scales: {
      x: { stacked: true, grid: { display: false } },
      y: {
        stacked: true,
        beginAtZero: true,
        grid: { color: 'rgba(148, 163, 184, 0.14)' },
        title: { display: true, text: 'Findings' },
      },
    },
    interaction: { intersect: false, mode: 'index' as const },
  }
}

/** Options for a ratio chart: a fixed 0–1 axis, so periods stay comparable. */
export function createRatioOptions() {
  return {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'top' as const },
      title: { display: false },
    },
    scales: {
      x: { grid: { display: false } },
      y: {
        beginAtZero: true,
        max: 1,
        grid: { color: 'rgba(148, 163, 184, 0.14)' },
        ticks: {
          callback: (value: string | number) => `${Math.round(Number(value) * 100)}%`,
        },
      },
    },
    interaction: { intersect: false, mode: 'index' as const },
  }
}
