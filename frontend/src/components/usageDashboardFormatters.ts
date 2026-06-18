// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

// Pure helpers, constants, and chart-data builders for UsageDashboard.vue.
// No reactive state lives here — stateful orchestration is in useUsageDashboard.ts.

import type { ClientTokenUsageResponse, ClientTokenUsageSample } from '@/types/clientTokenUsage'
import type {
  ProCursorTokenUsageBreakdownItemDto,
  ProCursorTokenUsageGroupBy,
  ProCursorTokenUsageResponse,
  ProCursorTopSourcesPeriod,
} from '@/types/proCursorTokenUsage'

// Chart.js needs literal colour values (it does not resolve CSS var()); keep these
// in sync with --chart-* / semantic tokens in tokens.css.
export const REVIEW_PALETTE = ['#4e91f3', '#f97316', '#22c55e', '#a855f7', '#ef4444', '#14b8a6', '#f59e0b', '#ec4899']
export const PROCURSOR_PALETTE = ['#f97316', '#4e91f3', '#14b8a6', '#f59e0b', '#3b7a57', '#a855f7']

export const CATEGORY_LABELS: Record<number, string> = {
  0: 'Low Effort',
  1: 'Medium Effort',
  2: 'High Effort',
  3: 'Embedding',
  4: 'Memory Reconsideration',
  5: 'Default',
}

export const PERIOD_PRESETS: Array<{ label: string; value: ProCursorTopSourcesPeriod }> = [
  { label: '30d', value: '30d' },
  { label: '90d', value: '90d' },
  { label: '365d', value: '365d' },
]

const integerFormatter = new Intl.NumberFormat('en-US')
const currencyFormatter = new Intl.NumberFormat('en-US', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

export function todayStr(): string {
  return new Date().toISOString().slice(0, 10)
}

export function daysAgoStr(days: number): string {
  const date = new Date()
  date.setDate(date.getDate() - days)
  return date.toISOString().slice(0, 10)
}

export function formatNumber(value: number | null | undefined): string {
  return integerFormatter.format(value ?? 0)
}

export function formatUsd(value: number | null | undefined): string {
  return value == null ? '—' : currencyFormatter.format(value)
}

export function formatDateTime(value: string | null | undefined): string {
  if (!value) {
    return ''
  }

  const date = new Date(value)
  return Number.isNaN(date.valueOf()) ? '' : date.toLocaleString()
}

export function formatBucketLabel(value: string | null | undefined): string {
  if (!value) {
    return ''
  }

  return value.slice(5)
}

export function getInclusiveDayCount(from: string, to: string): number {
  const fromDateValue = new Date(`${from}T00:00:00Z`)
  const toDateValue = new Date(`${to}T00:00:00Z`)

  if (Number.isNaN(fromDateValue.valueOf()) || Number.isNaN(toDateValue.valueOf())) {
    return 30
  }

  const diff = Math.round((toDateValue.valueOf() - fromDateValue.valueOf()) / 86_400_000) + 1
  return Math.max(1, diff)
}

export function getReviewSeriesKey(sample: ClientTokenUsageSample): string {
  return `${sample.connectionCategory ?? 5}_${sample.modelId}`
}

export function getReviewSeriesLabel(sample: ClientTokenUsageSample): string {
  const categoryName = CATEGORY_LABELS[sample.connectionCategory ?? 5] ?? 'Unknown'
  return `${categoryName} (${sample.modelId})`
}

export function getProCursorBreakdownKey(
  item: ProCursorTokenUsageBreakdownItemDto,
  groupBy: ProCursorTokenUsageGroupBy,
): string {
  if (groupBy === 'model') {
    return item.modelName || 'Unknown model'
  }

  return item.sourceDisplayName || 'Unattributed source'
}

/** Shared line-chart options. A fresh object per call so each chart owns its config. */
export function createChartOptions() {
  return {
    responsive: true,
    maintainAspectRatio: false,
    plugins: {
      legend: { position: 'top' as const },
      title: { display: false },
    },
    scales: {
      y: {
        beginAtZero: true,
        grid: { color: 'rgba(148, 163, 184, 0.14)' },
        title: { display: true, text: 'Total Tokens' },
      },
      x: {
        grid: { display: false },
      },
    },
    interaction: {
      intersect: false,
      mode: 'index' as const,
    },
  }
}

export function buildReviewChartData(usage: ClientTokenUsageResponse | null, hasSamples: boolean) {
  if (!usage || !hasSamples) {
    return { labels: [], datasets: [] }
  }

  const datesSet = new Set<string>()
  const seriesSet = new Map<string, string>()
  const lookup = new Map<string, Map<string, ClientTokenUsageSample>>()

  for (const sample of usage.samples) {
    datesSet.add(sample.date)
    const key = getReviewSeriesKey(sample)

    if (!seriesSet.has(key)) {
      seriesSet.set(key, getReviewSeriesLabel(sample))
    }

    if (!lookup.has(key)) {
      lookup.set(key, new Map())
    }

    lookup.get(key)?.set(sample.date, sample)
  }

  const labels = Array.from(datesSet).sort()
  const datasets = Array.from(seriesSet.entries()).map(([key, label], index) => {
    const color = REVIEW_PALETTE[index % REVIEW_PALETTE.length]
    const samplesByDate = lookup.get(key)
    return {
      label,
      data: labels.map((date) => {
        const sample = samplesByDate?.get(date)
        return (sample?.inputTokens ?? 0) + (sample?.outputTokens ?? 0)
      }),
      borderColor: color,
      backgroundColor: `${color}22`,
      tension: 0.35,
      fill: true,
      pointRadius: 0,
      pointHoverRadius: 5,
    }
  })

  return { labels, datasets }
}

export function buildProCursorChartData(
  usage: ProCursorTokenUsageResponse | null,
  groupBy: ProCursorTokenUsageGroupBy,
) {
  const series = usage?.series ?? []
  if (series.length === 0) {
    return { labels: [], datasets: [] }
  }

  const labels = series.map((point) => formatBucketLabel(point.bucketStart))
  const datasetsMap = new Map<string, { label: string; data: number[]; total: number }>()

  series.forEach((point, index) => {
    const bucketValues = new Map<string, number>()
    const breakdown = point.breakdown ?? []

    if (breakdown.length === 0) {
      bucketValues.set('Total tokens', point.totalTokens ?? 0)
    } else {
      breakdown.forEach((item) => {
        const key = getProCursorBreakdownKey(item, groupBy)
        bucketValues.set(key, (bucketValues.get(key) ?? 0) + (item.totalTokens ?? 0))
      })
    }

    bucketValues.forEach((value, key) => {
      if (!datasetsMap.has(key)) {
        datasetsMap.set(key, {
          label: key,
          data: Array.from({ length: series.length }, () => 0),
          total: 0,
        })
      }

      const dataset = datasetsMap.get(key)
      if (!dataset) {
        return
      }

      dataset.data[index] = value
      dataset.total += value
    })
  })

  const datasets = Array.from(datasetsMap.values())
    .sort((left, right) => right.total - left.total)
    .map((dataset, index) => {
      const color = PROCURSOR_PALETTE[index % PROCURSOR_PALETTE.length]
      return {
        label: dataset.label,
        data: dataset.data,
        borderColor: color,
        backgroundColor: `${color}24`,
        tension: 0.28,
        fill: true,
        pointRadius: 0,
        pointHoverRadius: 5,
      }
    })

  return { labels, datasets }
}
