// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { useTenantBudgetOverview } from '@/features/tenants/view-models/useTenantBudgetOverview'
import type { TenantBudgetOverview } from '@/services/tenantBudgetOverviewService'

function overview(): TenantBudgetOverview {
  return {
    tenantId: 't1',
    periodStart: '2026-07-01',
    periodEnd: '2026-07-31',
    asOf: '2026-07-15',
    clients: [
      { clientId: 'a', displayName: 'Acme', spentToDateUsd: 30, monthlySoftCapUsd: 80, monthlyHardCapUsd: 100, projectedPeriodSpendUsd: 60 },
      { clientId: 'b', displayName: 'Globex', spentToDateUsd: 110, monthlySoftCapUsd: 80, monthlyHardCapUsd: 100, projectedPeriodSpendUsd: 130 },
      { clientId: 'c', displayName: 'Umbrella', spentToDateUsd: 20, monthlySoftCapUsd: null, monthlyHardCapUsd: null, projectedPeriodSpendUsd: 40 },
    ],
  }
}

describe('useTenantBudgetOverview', () => {
  it('loads, derives per-row status/meter, and sorts by spend descending by default', async () => {
    const vm = useTenantBudgetOverview('t1', { loader: async () => ({ data: overview() }) })
    await vm.loadOverview()

    const rows = vm.rows.value
    expect(rows.map((r) => r.displayName)).toEqual(['Globex', 'Acme', 'Umbrella'])
    // Globex is over the hard cap → danger, meter clamped to 100%.
    expect(rows[0].status).toBe('danger')
    expect(rows[0].meterPercent).toBe(100)
    // Acme is under both caps → ok, 30 of 100 = 30%.
    expect(rows[1].status).toBe('ok')
    expect(rows[1].meterPercent).toBe(30)
    // Umbrella has no configured budget.
    expect(rows[2].hasBudget).toBe(false)
    expect(rows[2].meterCapUsd).toBeNull()
  })

  it('filters rows by client name (case-insensitive)', async () => {
    const vm = useTenantBudgetOverview('t1', { loader: async () => ({ data: overview() }) })
    await vm.loadOverview()

    vm.search.value = 'glob'
    expect(vm.rows.value.map((r) => r.displayName)).toEqual(['Globex'])
  })

  it('sorts by name and by utilization', async () => {
    const vm = useTenantBudgetOverview('t1', { loader: async () => ({ data: overview() }) })
    await vm.loadOverview()

    vm.sortBy.value = 'name'
    expect(vm.rows.value.map((r) => r.displayName)).toEqual(['Acme', 'Globex', 'Umbrella'])

    vm.sortBy.value = 'utilization'
    // Globex (100%) then Acme (30%) then Umbrella (no cap → 0%).
    expect(vm.rows.value.map((r) => r.displayName)).toEqual(['Globex', 'Acme', 'Umbrella'])
  })

  it('reports an error when loading fails', async () => {
    const vm = useTenantBudgetOverview('t1', { loader: async () => ({ error: 'boom' }) })
    await vm.loadOverview()

    expect(vm.overview.value).toBeNull()
    expect(vm.error.value).not.toBe('')
  })
})
