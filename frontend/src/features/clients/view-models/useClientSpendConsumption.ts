// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import type { ChartData } from 'chart.js'
import {
  getClientBudgetConsumption,
  getClientBudgetHistory,
  type ClientBudgetConsumption,
  type ClientBudgetHistory,
} from '@/services/budgetConsumptionService'

export interface SpendConsumptionLoadResult {
  data?: ClientBudgetConsumption | null
  error?: unknown
}

export interface SpendHistoryLoadResult {
  data?: ClientBudgetHistory | null
  error?: unknown
}

export interface UseClientSpendConsumptionOptions {
  /** Overridable single-period loader for tests; defaults to the live budget-consumption endpoint. */
  loader?: (clientId: string, period?: string) => Promise<SpendConsumptionLoadResult>
  /** Overridable history loader for tests; defaults to the live budget-history endpoint. */
  historyLoader?: (clientId: string, months: number) => Promise<SpendHistoryLoadResult>
  /** Trailing months of history to request (default 12). */
  monthsBack?: number
}

// Chart.js draws to a <canvas> and does not resolve CSS var(), so series colours must be literal strings
// here rather than tokens. Keep these in lockstep with the matching custom properties in tokens.css:
// spend = --chart-1, projection = --color-suggestion, soft cap = --color-warning, hard cap = --color-danger.
const SPEND_COLOR = '#4e91f3'
const PROJECTION_COLOR = '#a855f7'
const SOFT_CAP_COLOR = '#f59e0b'
const HARD_CAP_COLOR = '#ef4444'

const MONTH_LABELS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']

function enumerateDays(startIso: string, endIso: string): string[] {
  const days: string[] = []
  const cursor = new Date(`${startIso}T00:00:00Z`)
  const end = new Date(`${endIso}T00:00:00Z`)
  if (Number.isNaN(cursor.valueOf()) || Number.isNaN(end.valueOf())) {
    return days
  }

  // Bound the loop defensively so a malformed range can never spin forever.
  for (let guard = 0; cursor <= end && guard < 400; guard += 1) {
    days.push(cursor.toISOString().slice(0, 10))
    cursor.setUTCDate(cursor.getUTCDate() + 1)
  }

  return days
}

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

export function useClientSpendConsumption(
  clientId: string,
  options: UseClientSpendConsumptionOptions = {},
) {
  const load = options.loader ?? getClientBudgetConsumption
  const loadHistoryFn = options.historyLoader ?? getClientBudgetHistory
  const monthsBack = options.monthsBack ?? 12

  const consumption = ref<ClientBudgetConsumption | null>(null)
  const loading = ref(false)
  const error = ref('')

  const history = ref<ClientBudgetHistory | null>(null)
  const historyLoading = ref(false)
  const historyError = ref('')

  // Period selection. Initialised to the current UTC month; the picker walks backwards from here and can never
  // advance past the current month.
  const now = new Date()
  const currentYear = now.getUTCFullYear()
  const currentMonth = now.getUTCMonth() + 1
  const selectedYear = ref(currentYear)
  const selectedMonth = ref(currentMonth)

  const isCurrentPeriod = computed(
    () => selectedYear.value === currentYear && selectedMonth.value === currentMonth,
  )
  const canGoToNextMonth = computed(() => !isCurrentPeriod.value)
  const periodLabel = computed(() =>
    new Date(Date.UTC(selectedYear.value, selectedMonth.value - 1, 1)).toLocaleDateString(undefined, {
      month: 'long',
      year: 'numeric',
      timeZone: 'UTC',
    }),
  )

  function selectedPeriodParam(): string {
    return `${selectedYear.value}-${String(selectedMonth.value).padStart(2, '0')}`
  }

  const spentToDateUsd = computed(() => consumption.value?.spentToDateUsd ?? 0)
  const softCapUsd = computed(() => consumption.value?.monthlySoftCapUsd ?? null)
  const hardCapUsd = computed(() => consumption.value?.monthlyHardCapUsd ?? null)
  const projectedPeriodSpendUsd = computed(() => consumption.value?.projectedPeriodSpendUsd ?? null)
  const spendIsApproximate = computed(() => consumption.value?.spendIsApproximate === true)

  const hasBudget = computed(() => softCapUsd.value != null || hardCapUsd.value != null)
  const isOverSoftCap = computed(() => softCapUsd.value != null && spentToDateUsd.value >= softCapUsd.value)
  const isOverHardCap = computed(() => hardCapUsd.value != null && spentToDateUsd.value >= hardCapUsd.value)
  const projectedToExceedSoftCap = computed(
    () => softCapUsd.value != null && projectedPeriodSpendUsd.value != null && projectedPeriodSpendUsd.value > softCapUsd.value,
  )
  const projectedToExceedHardCap = computed(
    () => hardCapUsd.value != null && projectedPeriodSpendUsd.value != null && projectedPeriodSpendUsd.value > hardCapUsd.value,
  )

  /** The cap the progress meter fills toward: the hard cap when set, otherwise the soft cap. */
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

  const spendChartData = computed<ChartData<'line'>>(() => {
    const current = consumption.value
    if (!current?.periodStart || !current.periodEnd || !current.asOf) {
      return { labels: [], datasets: [] }
    }

    const days = enumerateDays(current.periodStart, current.periodEnd)
    const dailyByDate = new Map((current.dailySpend ?? []).map((d) => [d.date, d.spentUsd ?? 0]))
    const asOfIndex = days.indexOf(current.asOf)
    const lastIndex = days.length - 1

    let cumulative = 0
    const actual = days.map((day, index) => {
      if (asOfIndex >= 0 && index > asOfIndex) {
        return null
      }
      cumulative += dailyByDate.get(day) ?? 0
      return cumulative
    })
    const spentAtAsOf = cumulative
    const projected = current.projectedPeriodSpendUsd ?? null

    const projection = days.map((_, index) => {
      if (projected == null || asOfIndex < 0 || index < asOfIndex) {
        return null
      }
      if (asOfIndex >= lastIndex) {
        return index === asOfIndex ? spentAtAsOf : null
      }
      const t = (index - asOfIndex) / (lastIndex - asOfIndex)
      return spentAtAsOf + t * (projected - spentAtAsOf)
    })

    const datasets: ChartData<'line'>['datasets'] = [
      {
        label: 'Cumulative spend',
        data: actual,
        borderColor: SPEND_COLOR,
        backgroundColor: `${SPEND_COLOR}22`,
        tension: 0.25,
        fill: true,
        pointRadius: 0,
        pointHoverRadius: 4,
      },
      {
        label: 'Projected',
        data: projection,
        borderColor: PROJECTION_COLOR,
        borderDash: [6, 4],
        tension: 0,
        fill: false,
        pointRadius: 0,
        pointHoverRadius: 4,
        spanGaps: true,
      },
    ]

    appendCapLines(datasets, days.length, current.monthlySoftCapUsd, current.monthlyHardCapUsd)
    return { labels: days.map((d) => d.slice(5)), datasets }
  })

  const historyChartData = computed<ChartData<'line'>>(() => {
    const months = history.value?.months
    if (!months?.length) {
      return { labels: [], datasets: [] }
    }

    const labels = months.map((m) => `${MONTH_LABELS[((m.month ?? 1) - 1) % 12]} '${String(m.year ?? 0).slice(-2)}`)
    const datasets: ChartData<'line'>['datasets'] = [
      {
        label: 'Monthly spend',
        data: months.map((m) => m.spentUsd ?? 0),
        borderColor: SPEND_COLOR,
        backgroundColor: `${SPEND_COLOR}22`,
        tension: 0.25,
        fill: true,
        pointRadius: 3,
        pointHoverRadius: 5,
      },
    ]

    appendCapLines(datasets, months.length, history.value?.monthlySoftCapUsd, history.value?.monthlyHardCapUsd)
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

  async function loadConsumption(): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      const { data, error: loadError } = await load(clientId, selectedPeriodParam())
      if (loadError || !data) {
        error.value = 'Failed to load budget consumption. Please try again.'
        return
      }
      consumption.value = data
    } catch {
      error.value = 'Failed to load budget consumption. Please try again.'
    } finally {
      loading.value = false
    }
  }

  async function loadHistory(): Promise<void> {
    historyLoading.value = true
    historyError.value = ''
    try {
      const { data, error: loadError } = await loadHistoryFn(clientId, monthsBack)
      if (loadError || !data) {
        historyError.value = 'Failed to load spend history.'
        return
      }
      history.value = data
    } catch {
      historyError.value = 'Failed to load spend history.'
    } finally {
      historyLoading.value = false
    }
  }

  async function goToPreviousMonth(): Promise<void> {
    if (selectedMonth.value === 1) {
      selectedMonth.value = 12
      selectedYear.value -= 1
    } else {
      selectedMonth.value -= 1
    }
    await loadConsumption()
  }

  async function goToNextMonth(): Promise<void> {
    if (!canGoToNextMonth.value) {
      return
    }
    if (selectedMonth.value === 12) {
      selectedMonth.value = 1
      selectedYear.value += 1
    } else {
      selectedMonth.value += 1
    }
    await loadConsumption()
  }

  async function goToCurrentMonth(): Promise<void> {
    if (isCurrentPeriod.value) {
      return
    }
    selectedYear.value = currentYear
    selectedMonth.value = currentMonth
    await loadConsumption()
  }

  return {
    consumption,
    loading,
    error,
    history,
    historyLoading,
    historyError,
    loadConsumption,
    loadHistory,
    selectedYear,
    selectedMonth,
    isCurrentPeriod,
    canGoToNextMonth,
    periodLabel,
    goToPreviousMonth,
    goToNextMonth,
    goToCurrentMonth,
    spentToDateUsd,
    softCapUsd,
    hardCapUsd,
    projectedPeriodSpendUsd,
    spendIsApproximate,
    hasBudget,
    isOverSoftCap,
    isOverHardCap,
    projectedToExceedSoftCap,
    projectedToExceedHardCap,
    meterCapUsd,
    meterPercent,
    remainingUsd,
    status,
    spendChartData,
    historyChartData,
    chartOptions,
    // Back-compat alias for the single-period chart options.
    spendChartOptions: chartOptions,
  }
}
