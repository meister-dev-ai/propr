// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { useTenantBudgetSpend } from '@/features/tenants/view-models/useTenantBudgetSpend'
import type { TenantSpend } from '@/services/tenantBudgetOverviewService'

function spend(overrides: Partial<TenantSpend> = {}): TenantSpend {
  return {
    tenantId: 't1',
    periodStart: '2026-07-01',
    periodEnd: '2026-07-31',
    asOf: '2026-07-15',
    spentToDateUsd: 90,
    monthlySoftCapUsd: 120,
    monthlyHardCapUsd: 150,
    projectedPeriodSpendUsd: 180,
    months: [
      { year: 2026, month: 6, periodStart: '2026-06-01', spentUsd: 100 },
      { year: 2026, month: 7, periodStart: '2026-07-01', spentUsd: 90 },
    ],
    ...overrides,
  }
}

describe('useTenantBudgetSpend', () => {
  it('loads and derives the aggregate meter and projection state', async () => {
    const vm = useTenantBudgetSpend('t1', { loader: async () => ({ data: spend() }) })
    await vm.loadSpend()

    expect(vm.spentToDateUsd.value).toBe(90)
    expect(vm.softCapUsd.value).toBe(120)
    expect(vm.hardCapUsd.value).toBe(150)
    // Meter fills toward the hard cap: 90 / 150 = 60%.
    expect(vm.meterPercent.value).toBe(60)
    expect(vm.status.value).toBe('ok')
    // Projection (180) exceeds both summed caps.
    expect(vm.projectedToExceedSoftCap.value).toBe(true)
    expect(vm.projectedToExceedHardCap.value).toBe(true)
  })

  it('flags danger and clamps the meter once over the summed hard cap', async () => {
    const vm = useTenantBudgetSpend('t1', { loader: async () => ({ data: spend({ spentToDateUsd: 200 }) }) })
    await vm.loadSpend()

    expect(vm.status.value).toBe('danger')
    expect(vm.meterPercent.value).toBe(100)
    expect(vm.remainingUsd.value).toBe(-50)
  })

  it('builds a trend line plus soft/hard cap reference lines', async () => {
    const vm = useTenantBudgetSpend('t1', { loader: async () => ({ data: spend() }) })
    await vm.loadSpend()

    const data = vm.trendChartData.value
    expect(data.labels).toEqual(["Jun '26", "Jul '26"])
    expect(data.datasets[0].data).toEqual([100, 90])
    // The trend line is followed by the two cap reference lines.
    expect(data.datasets.map((d) => d.label)).toEqual(['Aggregate spend', 'Soft cap', 'Hard cap'])
  })

  it('reports no budget when no client has caps configured', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({ data: spend({ monthlySoftCapUsd: null, monthlyHardCapUsd: null }) }),
    })
    await vm.loadSpend()

    expect(vm.hasBudget.value).toBe(false)
    expect(vm.meterCapUsd.value).toBeNull()
    expect(vm.remainingUsd.value).toBeNull()
    // With no caps, no reference lines are drawn.
    expect(vm.trendChartData.value.datasets.map((d) => d.label)).toEqual(['Aggregate spend'])
  })

  it('reports an error when loading fails', async () => {
    const vm = useTenantBudgetSpend('t1', { loader: async () => ({ error: 'boom' }) })
    await vm.loadSpend()

    expect(vm.spend.value).toBeNull()
    expect(vm.error.value).not.toBe('')
  })
})
