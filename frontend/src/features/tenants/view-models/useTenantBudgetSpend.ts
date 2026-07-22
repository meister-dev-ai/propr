// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import type { ChartData } from 'chart.js'
import { getTenantBudgetSpend, type TenantSpend } from '@/services/tenantBudgetOverviewService'

export interface TenantSpendLoadResult {
  data?: TenantSpend | null
  error?: unknown
}

export interface UseTenantBudgetSpendOptions {
  /** Overridable loader for tests; defaults to the live tenant-spend endpoint. */
  loader?: (tenantId: string, months: number) => Promise<TenantSpendLoadResult>
  /** Trailing months of trend to request (default 12). */
  monthsBack?: number
}

// Chart.js draws to a <canvas> and does not resolve CSS var(), so series colours must be literal strings
// here rather than tokens. Keep these in lockstep with the matching custom properties in tokens.css:
// spend = --chart-1, soft cap = --color-warning, hard cap = --color-danger.
const SPEND_COLOR = '#4e91f3'
const SOFT_CAP_COLOR = '#f59e0b'
const HARD_CAP_COLOR = '#ef4444'

const MONTH_LABELS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

/** Appends flat soft/hard cap reference-line datasets (when configured) across a chart of the given length. */
function appendCapLines(
  datasets: ChartData<'line'>['datasets'],
  length: number,
  softCapUsd: number | null | undefined,
  hardCapUsd: number | null | undefined,
): void {
  if (softCapUsd != null) {
    datasets.push({
      label: 'Soft cap',
      data: Array.from({ length }, () => softCapUsd),
      borderColor: SOFT_CAP_COLOR,
      borderDash: [3, 3],
      fill: false,
      pointRadius: 0,
      tension: 0,
    })
  }

  if (hardCapUsd != null) {
    datasets.push({
      label: 'Hard cap',
      data: Array.from({ length }, () => hardCapUsd),
      borderColor: HARD_CAP_COLOR,
      borderDash: [3, 3],
      fill: false,
      pointRadius: 0,
      tension: 0,
    })
  }
}

export function useTenantBudgetSpend(tenantId: string, options: UseTenantBudgetSpendOptions = {}) {
  const load = options.loader ?? getTenantBudgetSpend
  const monthsBack = options.monthsBack ?? 12

  const spend = ref<TenantSpend | null>(null)
  const loading = ref(false)
  const error = ref('')

  const spentToDateUsd = computed(() => spend.value?.spentToDateUsd ?? 0)
  const softCapUsd = computed(() => spend.value?.monthlySoftCapUsd ?? null)
  const hardCapUsd = computed(() => spend.value?.monthlyHardCapUsd ?? null)
  const projectedPeriodSpendUsd = computed(() => spend.value?.projectedPeriodSpendUsd ?? null)

  const hasBudget = computed(() => softCapUsd.value != null || hardCapUsd.value != null)
  const isOverSoftCap = computed(() => softCapUsd.value != null && spentToDateUsd.value >= softCapUsd.value)
  const isOverHardCap = computed(() => hardCapUsd.value != null && spentToDateUsd.value >= hardCapUsd.value)
  const projectedToExceedSoftCap = computed(
    () => softCapUsd.value != null && projectedPeriodSpendUsd.value != null && projectedPeriodSpendUsd.value > softCapUsd.value,
  )
  const projectedToExceedHardCap = computed(
    () => hardCapUsd.value != null && projectedPeriodSpendUsd.value != null && projectedPeriodSpendUsd.value > hardCapUsd.value,
  )

  /** The cap the progress meter fills toward: the summed hard cap when set, otherwise the summed soft cap. */
  const meterCapUsd = computed(() => hardCapUsd.value ?? softCapUsd.value)
  const meterPercent = computed(() => {
    const cap = meterCapUsd.value
    if (cap == null || cap <= 0) {
      return 0
    }
    return Math.min(100, (spentToDateUsd.value / cap) * 100)
  })
  const remainingUsd = computed(() => {
    const cap = meterCapUsd.value
    return cap == null ? null : cap - spentToDateUsd.value
  })
  const status = computed<'ok' | 'warning' | 'danger'>(() => {
    if (isOverHardCap.value) {
      return 'danger'
    }
    if (isOverSoftCap.value) {
      return 'warning'
    }
    return 'ok'
  })

  const trendChartData = computed<ChartData<'line'>>(() => {
    const months = spend.value?.months
    if (!months?.length) {
      return { labels: [], datasets: [] }
    }

    const labels = months.map((m) => `${MONTH_LABELS[((m.month ?? 1) - 1) % 12]} '${String(m.year ?? 0).slice(-2)}`)
    const datasets: ChartData<'line'>['datasets'] = [
      {
        label: 'Aggregate spend',
        data: months.map((m) => m.spentUsd ?? 0),
        borderColor: SPEND_COLOR,
        backgroundColor: `${SPEND_COLOR}22`,
        tension: 0.25,
        fill: true,
        pointRadius: 3,
        pointHoverRadius: 5,
      },
    ]

    appendCapLines(datasets, months.length, softCapUsd.value, hardCapUsd.value)
    return { labels, datasets }
  })

  const chartOptions = computed(() => ({
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
        title: { display: true, text: 'USD' },
      },
      x: {
        grid: { display: false },
      },
    },
    interaction: {
      intersect: false,
      mode: 'index' as const,
    },
  }))

  async function loadSpend(): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      const { data, error: loadError } = await load(tenantId, monthsBack)
      if (loadError || !data) {
        error.value = 'Failed to load tenant spend. Please try again.'
        return
      }
      spend.value = data
    } catch {
      error.value = 'Failed to load tenant spend. Please try again.'
    } finally {
      loading.value = false
    }
  }

  return {
    spend,
    loading,
    error,
    spentToDateUsd,
    softCapUsd,
    hardCapUsd,
    projectedPeriodSpendUsd,
    hasBudget,
    isOverSoftCap,
    isOverHardCap,
    projectedToExceedSoftCap,
    projectedToExceedHardCap,
    meterCapUsd,
    meterPercent,
    remainingUsd,
    status,
    trendChartData,
    chartOptions,
    loadSpend,
  }
}
