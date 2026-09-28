// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import type { ChartData } from 'chart.js'
import {
  getTenantBudgetSpend,
  type TenantSpend,
  type TenantSpendMonth,
} from '@/services/tenantBudgetOverviewService'

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
/**
 * Appends cap reference lines from per-point values, so a month whose clients received a manual reset steps up
 * instead of stretching today's raised ceiling back across months it was never in force for.
 */
function appendCapSeries(
  datasets: ChartData<'line'>['datasets'],
  softCaps: (number | null)[],
  hardCaps: (number | null)[],
): void {
  if (softCaps.some((value) => value != null)) {
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

  if (hardCaps.some((value) => value != null)) {
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

  // The tenant's own caps, which enforcement applies to this aggregate. The summed client caps above stay a
  // reference total, so both are reported and the meter fills toward the tenant cap when one is set.
  const tenantSoftCapUsd = computed(() => spend.value?.tenantMonthlySoftCapUsd ?? null)
  const tenantHardCapUsd = computed(() => spend.value?.tenantMonthlyHardCapUsd ?? null)
  const hasTenantCap = computed(() => tenantSoftCapUsd.value != null || tenantHardCapUsd.value != null)

  // The caps enforcement applies to this aggregate. A tenant that states either kind is measured against its
  // own caps alone, so the kind it left blank has no ceiling here; the summed client caps are a reference
  // total that binds a single client each, and they stand in only for a tenant that states neither kind.
  const effectiveSoftCapUsd = computed(() => (hasTenantCap.value ? tenantSoftCapUsd.value : softCapUsd.value))
  const effectiveHardCapUsd = computed(() => (hasTenantCap.value ? tenantHardCapUsd.value : hardCapUsd.value))

  const hasBudget = computed(() => effectiveSoftCapUsd.value != null || effectiveHardCapUsd.value != null)

  // Manual spend resets this period across the tenant's clients. The summed caps above already include their
  // allowance, so this exists to explain a raised total rather than to adjust it.
  const resetCount = computed(() => spend.value?.resetCount ?? 0)
  const hasResets = computed(() => resetCount.value > 0)
  const lastResetAt = computed(() => spend.value?.lastResetAt ?? null)

  const isOverSoftCap = computed(
    () => effectiveSoftCapUsd.value != null && spentToDateUsd.value >= effectiveSoftCapUsd.value,
  )
  const isOverHardCap = computed(
    () => effectiveHardCapUsd.value != null && spentToDateUsd.value >= effectiveHardCapUsd.value,
  )
  const projectedToExceedSoftCap = computed(
    () =>
      effectiveSoftCapUsd.value != null &&
      projectedPeriodSpendUsd.value != null &&
      projectedPeriodSpendUsd.value > effectiveSoftCapUsd.value,
  )
  const projectedToExceedHardCap = computed(
    () =>
      effectiveHardCapUsd.value != null &&
      projectedPeriodSpendUsd.value != null &&
      projectedPeriodSpendUsd.value > effectiveHardCapUsd.value,
  )

  /**
   * The cap the progress meter fills toward: the effective hard cap, or the effective soft cap when no hard
   * cap is in force. Both come from the same source as the status above, so the meter and the status cannot
   * report against caps of different origins.
   */
  const meterCapUsd = computed(() => effectiveHardCapUsd.value ?? effectiveSoftCapUsd.value)
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

  /** Whether the trend point covers the period the current tenant caps are in force for. */
  function isCurrentMonth(month: TenantSpendMonth): boolean {
    const periodStart = spend.value?.periodStart
    if (!periodStart) {
      return false
    }
    const [year, monthNumber] = periodStart.split('-')
    return month.year === Number(year) && month.month === Number(monthNumber)
  }

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

    // The API carries per-month cap history for the client caps only, so a tenant cap is drawn on the current
    // month alone. Stretching today's tenant cap back over earlier months would show them breaching a ceiling
    // that was not in force then.
    appendCapSeries(
      datasets,
      months.map((m) =>
        isCurrentMonth(m)
          ? (tenantSoftCapUsd.value ?? m.effectiveSoftCapUsd ?? softCapUsd.value)
          : (m.effectiveSoftCapUsd ?? softCapUsd.value),
      ),
      months.map((m) =>
        isCurrentMonth(m)
          ? (tenantHardCapUsd.value ?? m.effectiveHardCapUsd ?? hardCapUsd.value)
          : (m.effectiveHardCapUsd ?? hardCapUsd.value),
      ),
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
    tenantSoftCapUsd,
    tenantHardCapUsd,
    hasTenantCap,
    effectiveSoftCapUsd,
    effectiveHardCapUsd,
    projectedPeriodSpendUsd,
    hasBudget,
    resetCount,
    hasResets,
    lastResetAt,
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
