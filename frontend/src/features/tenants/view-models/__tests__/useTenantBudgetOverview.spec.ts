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

  it('carries each row reset count and meters against the cap in force', async () => {
    const data = overview()
    data.clients = [
      {
        clientId: 'a',
        displayName: 'Acme',
        spentToDateUsd: 95,
        monthlySoftCapUsd: 160,
        monthlyHardCapUsd: 200,
        projectedPeriodSpendUsd: 190,
        resetCount: 1,
      },
    ]
    const vm = useTenantBudgetOverview('t1', { loader: async () => ({ data }) })

    await vm.loadOverview()

    const row = vm.rows.value[0]
    expect(row.resetCount).toBe(1)
    // 95 of the topped-up 200 → still ok, not the 95% it would be against the configured 100.
    expect(row.meterCapUsd).toBe(200)
    expect(row.meterPercent).toBe(47.5)
    expect(row.status).toBe('ok')
  })

  it('defaults the reset count to zero when the payload omits it', async () => {
    const vm = useTenantBudgetOverview('t1', { loader: async () => ({ data: overview() }) })

    await vm.loadOverview()

    expect(vm.rows.value.every((row) => row.resetCount === 0)).toBe(true)
  })

  it('reloads the overview after resetting one client', async () => {
    let loads = 0
    const resetClients: string[] = []
    const vm = useTenantBudgetOverview('t1', {
      loader: async () => {
        loads += 1
        return { data: overview() }
      },
      resetAction: async (clientId) => {
        resetClients.push(clientId)
        return { data: { id: 'r1', periodStart: '2026-07-01' } }
      },
    })

    await vm.loadOverview()
    const ok = await vm.resetClientSpend('b')

    expect(ok).toBe(true)
    // Only the named client is reset — the tenant view is an entry point, not a bulk action.
    expect(resetClients).toEqual(['b'])
    expect(loads).toBe(2)
    expect(vm.resettingClientId.value).toBeNull()
  })

  it('surfaces an error and does not reload when a client reset fails', async () => {
    let loads = 0
    const vm = useTenantBudgetOverview('t1', {
      loader: async () => {
        loads += 1
        return { data: overview() }
      },
      resetAction: async () => ({ error: 'boom' }),
    })

    await vm.loadOverview()
    const ok = await vm.resetClientSpend('b')

    expect(ok).toBe(false)
    expect(loads).toBe(1)
    expect(vm.resetError.value).not.toBe('')
  })
})
