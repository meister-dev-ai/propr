// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import type { ChartData } from 'chart.js'
import {
  getClientBudgetConsumption,
  getClientBudgetHistory,
  resetClientBudgetSpend,
  type BudgetSpendReset,
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

export interface SpendResetResult {
  data?: BudgetSpendReset | null
  error?: unknown
}

export interface UseClientSpendConsumptionOptions {
  /** Overridable single-period loader for tests; defaults to the live budget-consumption endpoint. */
  loader?: (clientId: string, period?: string) => Promise<SpendConsumptionLoadResult>
  /** Overridable history loader for tests; defaults to the live budget-history endpoint. */
  historyLoader?: (clientId: string, months: number) => Promise<SpendHistoryLoadResult>
  /** Overridable reset action for tests; defaults to the live budget-reset endpoint. */
  resetAction?: (clientId: string) => Promise<SpendResetResult>
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
  appendCapSeries(
    datasets,
    softCapUsd == null ? null : Array.from({ length }, () => softCapUsd),
    hardCapUsd == null ? null : Array.from({ length }, () => hardCapUsd),
  )
}

/**
 * Appends cap reference lines from explicit per-point values, so a cap that changed across the window (a month that
 * received a manual reset) renders as a step rather than one flat line.
 */
function appendCapSeries(
  datasets: ChartData<'line'>['datasets'],
  softCaps: (number | null)[] | null,
  hardCaps: (number | null)[] | null,
): void {
  if (softCaps?.some((value) => value != null)) {
    datasets.push({
      label: 'Soft cap',
      data: softCaps,
      borderColor: SOFT_CAP_COLOR,
      borderDash: [3, 3],
      fill: false,
      pointRadius: 0,
      tension: 0,
      spanGaps: true,
      stepped: true,
    })
  }

  if (hardCaps?.some((value) => value != null)) {
    datasets.push({
      label: 'Hard cap',
      data: hardCaps,
      borderColor: HARD_CAP_COLOR,
      borderDash: [3, 3],
      fill: false,
      pointRadius: 0,
      tension: 0,
      spanGaps: true,
      stepped: true,
    })
  }
}

export function useClientSpendConsumption(
  clientId: string,
  options: UseClientSpendConsumptionOptions = {},
) {
  const load = options.loader ?? getClientBudgetConsumption
  const loadHistoryFn = options.historyLoader ?? getClientBudgetHistory
  const resetFn = options.resetAction ?? resetClientBudgetSpend
  const monthsBack = options.monthsBack ?? 12

  const consumption = ref<ClientBudgetConsumption | null>(null)
  const loading = ref(false)
  const error = ref('')

  const history = ref<ClientBudgetHistory | null>(null)
  const historyLoading = ref(false)
  const historyError = ref('')

  const resetting = ref(false)
  const resetError = ref('')

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

  // Manual spend resets recorded for the selected period. The caps above already include their allowance, so these
  // exist to explain the raised ceiling rather than to adjust it.
  const resets = computed<BudgetSpendReset[]>(() => consumption.value?.resets ?? [])
  const resetCount = computed(() => resets.value.length)
  const hasResets = computed(() => resetCount.value > 0)
  /** A reset grants allowance to the period in progress, so it is offered on the current period only. */
  const canReset = computed(() => isCurrentPeriod.value && hasBudget.value)
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
  /**
   * The same scope as {@link meterCapUsd} but as configured, before any reset allowance — what a further reset would
   * grant. Kept separate so nothing has to divide the cap in force to guess it.
   */
  const configuredMeterCapUsd = computed(() =>
    hardCapUsd.value != null
      ? (consumption.value?.configuredHardCapUsd ?? null)
      : (consumption.value?.configuredSoftCapUsd ?? null),
  )
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

    // Each month carries the cap that was in force for it, so a reset month steps up instead of flattening the line.
    appendCapSeries(
      datasets,
      months.map((m) => m.effectiveSoftCapUsd ?? history.value?.monthlySoftCapUsd ?? null),
      months.map((m) => m.effectiveHardCapUsd ?? history.value?.monthlyHardCapUsd ?? null),
    )
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

  /**
   * Grants the current period a fresh allowance, then reloads both views so the meter, the period marker and the
   * history chart all read against the raised cap.
   */
  async function performReset(): Promise<boolean> {
    if (!canReset.value || resetting.value) {
      return false
    }

    // The server always resets the month IT considers current. A tab left open across a UTC month boundary still
    // believes the old month is current, so re-read the clock and refuse rather than grant an unseen period.
    const nowUtc = new Date()
    if (nowUtc.getUTCFullYear() !== selectedYear.value || nowUtc.getUTCMonth() + 1 !== selectedMonth.value) {
      resetError.value = 'The current month has changed since this page was opened. Reload before resetting.'
      return false
    }

    resetting.value = true
    resetError.value = ''
    try {
      const { data, error: actionError } = await resetFn(clientId)
      if (actionError || !data) {
        resetError.value = 'Failed to reset the spend for this period. Please try again.'
        return false
      }
      await Promise.all([loadConsumption(), loadHistory()])
      return true
    } catch {
      resetError.value = 'Failed to reset the spend for this period. Please try again.'
      return false
    } finally {
      resetting.value = false
    }
  }

  /** A reset failure belongs to the period it was attempted on, so it must not follow the picker to another month. */
  function clearResetError(): void {
    resetError.value = ''
  }

  async function goToPreviousMonth(): Promise<void> {
    clearResetError()
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
    clearResetError()
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
    clearResetError()
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
    resets,
    resetCount,
    hasResets,
    canReset,
    resetting,
    resetError,
    performReset,
    isOverSoftCap,
    isOverHardCap,
    projectedToExceedSoftCap,
    projectedToExceedHardCap,
    meterCapUsd,
    configuredMeterCapUsd,
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
