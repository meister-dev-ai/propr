// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import {
  getTenantBudgetOverview,
  type TenantBudgetOverview,
  type TenantBudgetOverviewClient,
} from '@/services/tenantBudgetOverviewService'

export interface TenantBudgetOverviewLoadResult {
  data?: TenantBudgetOverview | null
  error?: unknown
}

export type OverviewSortKey = 'spend' | 'utilization' | 'name'

export interface OverviewRow {
  clientId: string
  displayName: string
  spentToDateUsd: number
  monthlySoftCapUsd: number | null
  monthlyHardCapUsd: number | null
  projectedPeriodSpendUsd: number | null
  hasBudget: boolean
  meterCapUsd: number | null
  meterPercent: number
  status: 'ok' | 'warning' | 'danger'
}

export interface UseTenantBudgetOverviewOptions {
  /** Overridable loader for tests; defaults to the live tenant-overview endpoint. */
  loader?: (tenantId: string) => Promise<TenantBudgetOverviewLoadResult>
}

function toRow(client: TenantBudgetOverviewClient): OverviewRow {
  const spent = client.spentToDateUsd ?? 0
  const soft = client.monthlySoftCapUsd ?? null
  const hard = client.monthlyHardCapUsd ?? null
  const meterCap = hard ?? soft
  const meterPercent = meterCap != null && meterCap > 0 ? Math.min(100, (spent / meterCap) * 100) : 0

  let status: OverviewRow['status'] = 'ok'
  if (hard != null && spent >= hard) {
    status = 'danger'
  } else if (soft != null && spent >= soft) {
    status = 'warning'
  }

  return {
    clientId: client.clientId ?? '',
    displayName: client.displayName ?? '',
    spentToDateUsd: spent,
    monthlySoftCapUsd: soft,
    monthlyHardCapUsd: hard,
    projectedPeriodSpendUsd: client.projectedPeriodSpendUsd ?? null,
    hasBudget: soft != null || hard != null,
    meterCapUsd: meterCap,
    meterPercent,
    status,
  }
}

export function useTenantBudgetOverview(tenantId: string, options: UseTenantBudgetOverviewOptions = {}) {
  const load = options.loader ?? getTenantBudgetOverview

  const overview = ref<TenantBudgetOverview | null>(null)
  const loading = ref(false)
  const error = ref('')
  const search = ref('')
  const sortBy = ref<OverviewSortKey>('spend')

  const allRows = computed<OverviewRow[]>(() => (overview.value?.clients ?? []).map(toRow))

  const rows = computed<OverviewRow[]>(() => {
    const term = search.value.trim().toLowerCase()
    const filtered = term
      ? allRows.value.filter((row) => row.displayName.toLowerCase().includes(term))
      : allRows.value.slice()

    switch (sortBy.value) {
      case 'name':
        return filtered.sort((a, b) => a.displayName.localeCompare(b.displayName))
      case 'utilization':
        return filtered.sort((a, b) => b.meterPercent - a.meterPercent)
      default:
        return filtered.sort((a, b) => b.spentToDateUsd - a.spentToDateUsd)
    }
  })

  async function loadOverview(): Promise<void> {
    loading.value = true
    error.value = ''
    try {
      const { data, error: loadError } = await load(tenantId)
      if (loadError || !data) {
        error.value = 'Failed to load the budget overview. Please try again.'
        return
      }
      overview.value = data
    } catch {
      error.value = 'Failed to load the budget overview. Please try again.'
    } finally {
      loading.value = false
    }
  }

  return {
    overview,
    loading,
    error,
    search,
    sortBy,
    rows,
    loadOverview,
  }
}
