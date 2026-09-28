// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

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
  it('steps the trend cap lines so past months keep the ceiling they actually had', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({
        data: spend({
          monthlySoftCapUsd: 220,
          monthlyHardCapUsd: 250,
          resetCount: 1,
          months: [
            {
              year: 2026,
              month: 6,
              periodStart: '2026-06-01',
              spentUsd: 100,
              effectiveSoftCapUsd: 120,
              effectiveHardCapUsd: 150,
              resetCount: 0,
            },
            {
              year: 2026,
              month: 7,
              periodStart: '2026-07-01',
              spentUsd: 90,
              effectiveSoftCapUsd: 220,
              effectiveHardCapUsd: 250,
              resetCount: 1,
            },
          ],
        }),
      }),
    })

    await vm.loadSpend()

    const chart = vm.trendChartData.value
    // June keeps 120/150; only July carries the granted allowance.
    expect(chart.datasets.find((set) => set.label === 'Soft cap')?.data).toEqual([120, 220])
    expect(chart.datasets.find((set) => set.label === 'Hard cap')?.data).toEqual([150, 250])
  })

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

  it('measures the meter against the tenant cap when the tenant has one', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({
        data: spend({
          spentToDateUsd: 4500,
          monthlySoftCapUsd: 120,
          monthlyHardCapUsd: 150,
          tenantMonthlySoftCapUsd: 4000,
          tenantMonthlyHardCapUsd: 5000,
        }),
      }),
    })

    await vm.loadSpend()

    expect(vm.hasTenantCap.value).toBe(true)
    expect(vm.meterCapUsd.value).toBe(5000)
    expect(vm.meterPercent.value).toBe(90)
    // Past the tenant soft cap but under the tenant hard cap.
    expect(vm.status.value).toBe('warning')
    // The summed client total is still reported beside it.
    expect(vm.softCapUsd.value).toBe(120)
    expect(vm.hardCapUsd.value).toBe(150)
    // The projection (180) is measured against the tenant caps, not the summed client caps it also exceeds.
    expect(vm.projectedToExceedSoftCap.value).toBe(false)
    expect(vm.projectedToExceedHardCap.value).toBe(false)
  })

  it('fills the meter toward the tenant soft cap when it is the only tenant cap', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({
        data: spend({ spentToDateUsd: 2000, tenantMonthlySoftCapUsd: 4000 }),
      }),
    })

    await vm.loadSpend()

    // The summed client hard cap (150) would misreport a tenant spending toward its own $4,000 ceiling.
    expect(vm.meterCapUsd.value).toBe(4000)
    expect(vm.meterPercent.value).toBe(50)
  })

  it('draws the tenant cap on the current month only', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({
        data: spend({
          tenantMonthlySoftCapUsd: 4000,
          tenantMonthlyHardCapUsd: 5000,
          months: [
            {
              year: 2026,
              month: 6,
              periodStart: '2026-06-01',
              spentUsd: 100,
              effectiveSoftCapUsd: 120,
              effectiveHardCapUsd: 150,
            },
            {
              year: 2026,
              month: 7,
              periodStart: '2026-07-01',
              spentUsd: 90,
              effectiveSoftCapUsd: 120,
              effectiveHardCapUsd: 150,
            },
          ],
        }),
      }),
    })

    await vm.loadSpend()

    const chart = vm.trendChartData.value
    // June keeps the client cap history the API supplied; July carries the tenant cap in force today.
    expect(chart.datasets.find((set) => set.label === 'Soft cap')?.data).toEqual([120, 4000])
    expect(chart.datasets.find((set) => set.label === 'Hard cap')?.data).toEqual([150, 5000])
  })

  // The summed client caps bind a single client each, so a tenant that states one kind of cap is measured
  // against that kind alone. Mixing the two sources warned a tenant well inside its own $4,000 ceiling that it
  // had passed a summed client hard cap of $150.
  it('leaves the kind the tenant did not state without a ceiling when it stated the other', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({
        data: spend({ spentToDateUsd: 2000, projectedPeriodSpendUsd: 3000, tenantMonthlySoftCapUsd: 4000 }),
      }),
    })

    await vm.loadSpend()

    expect(vm.effectiveSoftCapUsd.value).toBe(4000)
    expect(vm.effectiveHardCapUsd.value).toBeNull()
    expect(vm.isOverHardCap.value).toBe(false)
    expect(vm.projectedToExceedHardCap.value).toBe(false)
    expect(vm.status.value).toBe('ok')
  })

  it('measures a tenant that stated only a hard cap against that cap alone', async () => {
    const vm = useTenantBudgetSpend('t1', {
      loader: async () => ({
        data: spend({ spentToDateUsd: 2000, projectedPeriodSpendUsd: 3000, tenantMonthlyHardCapUsd: 5000 }),
      }),
    })

    await vm.loadSpend()

    expect(vm.effectiveSoftCapUsd.value).toBeNull()
    expect(vm.effectiveHardCapUsd.value).toBe(5000)
    expect(vm.meterCapUsd.value).toBe(5000)
    expect(vm.isOverSoftCap.value).toBe(false)
    expect(vm.projectedToExceedSoftCap.value).toBe(false)
    expect(vm.status.value).toBe('ok')
  })

  it('falls back to the summed client caps when the tenant has none', async () => {
    const vm = useTenantBudgetSpend('t1', { loader: async () => ({ data: spend({ spentToDateUsd: 90 }) }) })

    await vm.loadSpend()

    expect(vm.hasTenantCap.value).toBe(false)
    expect(vm.effectiveSoftCapUsd.value).toBe(120)
    expect(vm.effectiveHardCapUsd.value).toBe(150)
    expect(vm.meterCapUsd.value).toBe(150)
  })
})
