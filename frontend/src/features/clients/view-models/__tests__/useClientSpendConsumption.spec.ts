// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { useClientSpendConsumption } from '@/features/clients/view-models/useClientSpendConsumption'
import type { ClientBudgetConsumption } from '@/services/budgetConsumptionService'

function fixture(overrides: Partial<ClientBudgetConsumption> = {}): ClientBudgetConsumption {
  return {
    clientId: 'c1',
    periodStart: '2026-07-01',
    periodEnd: '2026-07-31',
    nextResetOn: '2026-08-01',
    asOf: '2026-07-10',
    spentToDateUsd: 42,
    spendIsApproximate: false,
    monthlySoftCapUsd: 80,
    monthlyHardCapUsd: 100,
    projectedPeriodSpendUsd: 130,
    dailySpend: [
      { date: '2026-07-01', spentUsd: 12 },
      { date: '2026-07-05', spentUsd: 20 },
      { date: '2026-07-10', spentUsd: 10 },
    ],
    ...overrides,
  }
}

describe('useClientSpendConsumption', () => {
  it('loads consumption and exposes derived spend/cap/forecast values', async () => {
    const data = fixture()
    const spend = useClientSpendConsumption('c1', { loader: async () => ({ data }) })

    await spend.loadConsumption()

    expect(spend.consumption.value).toEqual(data)
    expect(spend.hasBudget.value).toBe(true)
    expect(spend.meterCapUsd.value).toBe(100)
    expect(spend.meterPercent.value).toBe(42)
    expect(spend.remainingUsd.value).toBe(58)
    expect(spend.status.value).toBe('ok')
    expect(spend.projectedToExceedSoftCap.value).toBe(true)
    expect(spend.projectedToExceedHardCap.value).toBe(true)
  })

  it('reports an error when the load fails', async () => {
    const spend = useClientSpendConsumption('c1', { loader: async () => ({ error: 'boom' }) })

    await spend.loadConsumption()

    expect(spend.consumption.value).toBeNull()
    expect(spend.error.value).not.toBe('')
  })

  it('flags danger status when spend is over the hard cap', async () => {
    const spend = useClientSpendConsumption('c1', {
      loader: async () => ({ data: fixture({ spentToDateUsd: 110 }) }),
    })

    await spend.loadConsumption()

    expect(spend.isOverHardCap.value).toBe(true)
    expect(spend.status.value).toBe('danger')
    expect(spend.remainingUsd.value).toBe(-10)
  })

  it('builds a cumulative spend line, a projection line, and both cap lines', async () => {
    const spend = useClientSpendConsumption('c1', { loader: async () => ({ data: fixture() }) })
    await spend.loadConsumption()

    const chart = spend.spendChartData.value
    expect(chart.labels).toHaveLength(31)
    expect(chart.datasets).toHaveLength(4)

    const [actual, projection, soft, hard] = chart.datasets as Array<{ label: string; data: (number | null)[] }>
    // Cumulative actual is populated through the as-of day (index 9) and null afterwards.
    expect(actual.data[0]).toBe(12)
    expect(actual.data[4]).toBe(32)
    expect(actual.data[9]).toBe(42)
    expect(actual.data[10]).toBeNull()
    // Projection starts at the as-of spend and reaches the projected total on the last day.
    expect(projection.data[8]).toBeNull()
    expect(projection.data[9]).toBe(42)
    expect(projection.data[30]).toBe(130)
    expect(soft.label).toBe('Soft cap')
    expect(soft.data.every((v) => v === 80)).toBe(true)
    expect(hard.data.every((v) => v === 100)).toBe(true)
  })

  it('omits caps and the meter when no budget is configured', async () => {
    const spend = useClientSpendConsumption('c1', {
      loader: async () => ({ data: fixture({ monthlySoftCapUsd: null, monthlyHardCapUsd: null }) }),
    })
    await spend.loadConsumption()

    expect(spend.hasBudget.value).toBe(false)
    expect(spend.meterCapUsd.value).toBeNull()
    expect(spend.remainingUsd.value).toBeNull()
    expect(spend.spendChartData.value.datasets).toHaveLength(2)
  })
})
