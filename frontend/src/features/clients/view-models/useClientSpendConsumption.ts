// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import type { ChartData } from 'chart.js'
import {
  getClientBudgetConsumption,
  type ClientBudgetConsumption,
} from '@/services/budgetConsumptionService'

export interface SpendConsumptionLoadResult {
  data?: ClientBudgetConsumption | null
  error?: unknown
}

export interface UseClientSpendConsumptionOptions {
  /** Overridable loader for tests; defaults to the live budget-consumption endpoint. */
  loader?: (clientId: string) => Promise<SpendConsumptionLoadResult>
}

const SPEND_COLOR = '#4e91f3'
const PROJECTION_COLOR = '#a855f7'
const SOFT_CAP_COLOR = '#f59e0b'
const HARD_CAP_COLOR = '#ef4444'

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

export function useClientSpendConsumption(
  clientId: string,
  options: UseClientSpendConsumptionOptions = {},
) {
  const load = options.loader ?? getClientBudgetConsumption

  const consumption = ref<ClientBudgetConsumption | null>(null)
  const loading = ref(false)
  const error = ref('')

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

  const spendChartData = computed(() => {
    const current = consumption.value
    if (!current?.periodStart || !current.periodEnd || !current.asOf) {
      return { labels: [] as string[], datasets: [] as ChartData<'line'>['datasets'] }
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

    const soft = current.monthlySoftCapUsd
    if (soft != null) {
      datasets.push({
        label: 'Soft cap',
        data: days.map(() => soft),
        borderColor: SOFT_CAP_COLOR,
        borderDash: [3, 3],
        fill: false,
        pointRadius: 0,
        tension: 0,
      })
    }

    const hard = current.monthlyHardCapUsd
    if (hard != null) {
      datasets.push({
        label: 'Hard cap',
        data: days.map(() => hard),
        borderColor: HARD_CAP_COLOR,
        borderDash: [3, 3],
        fill: false,
        pointRadius: 0,
        tension: 0,
      })
    }

    return { labels: days.map((d) => d.slice(5)), datasets }
  })

  const spendChartOptions = computed(() => ({
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
      const { data, error: loadError } = await load(clientId)
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

  return {
    consumption,
    loading,
    error,
    loadConsumption,
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
    spendChartOptions,
  }
}
