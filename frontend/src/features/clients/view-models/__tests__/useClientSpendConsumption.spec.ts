// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { useClientSpendConsumption } from '@/features/clients/view-models/useClientSpendConsumption'
import type {
  BudgetSpendReset,
  ClientBudgetConsumption,
  ClientBudgetHistory,
} from '@/services/budgetConsumptionService'

function resetFixture(overrides: Partial<BudgetSpendReset> = {}): BudgetSpendReset {
  return {
    id: 'r1',
    periodStart: '2026-07-01',
    topUpSoftCapUsd: 80,
    topUpHardCapUsd: 100,
    effectiveSoftCapBeforeUsd: 80,
    effectiveSoftCapAfterUsd: 160,
    effectiveHardCapBeforeUsd: 100,
    effectiveHardCapAfterUsd: 200,
    actorUserId: 'u1',
    actorUsername: 'saen',
    performedAt: '2026-07-15T09:14:00Z',
    ...overrides,
  }
}

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

  it('loads history and builds a monthly chart with cap reference lines', async () => {
    const historyData: ClientBudgetHistory = {
      clientId: 'c1',
      monthlySoftCapUsd: 80,
      monthlyHardCapUsd: 100,
      months: [
        { year: 2026, month: 6, periodStart: '2026-06-01', spentUsd: 55, spendIsApproximate: false },
        { year: 2026, month: 7, periodStart: '2026-07-01', spentUsd: 42, spendIsApproximate: false },
      ],
    }
    const spend = useClientSpendConsumption('c1', { historyLoader: async () => ({ data: historyData }) })

    await spend.loadHistory()

    expect(spend.history.value).toEqual(historyData)
    const chart = spend.historyChartData.value
    expect(chart.labels).toHaveLength(2)
    // Monthly spend line + soft cap + hard cap reference lines.
    expect(chart.datasets).toHaveLength(3)
    expect((chart.datasets[0].data as number[])).toEqual([55, 42])
  })

  it('requests the selected month when the picker steps to the previous month', async () => {
    const periods: (string | undefined)[] = []
    const spend = useClientSpendConsumption('c1', {
      loader: async (_clientId, period) => {
        periods.push(period)
        return { data: fixture() }
      },
    })

    await spend.loadConsumption()
    const first = periods.at(-1)
    await spend.goToPreviousMonth()
    const afterBack = periods.at(-1)

    expect(first).toMatch(/^\d{4}-\d{2}$/)
    expect(afterBack).toMatch(/^\d{4}-\d{2}$/)
    // Stepping back changes the requested period and the picker is no longer on the current month.
    expect(afterBack).not.toBe(first)
    expect(spend.isCurrentPeriod.value).toBe(false)
  })

  it('exposes the period resets and reads the meter against the cumulative cap', async () => {
    const spend = useClientSpendConsumption('c1', {
      loader: async () => ({
        data: fixture({
          monthlySoftCapUsd: 160,
          monthlyHardCapUsd: 200,
          configuredSoftCapUsd: 80,
          configuredHardCapUsd: 100,
          spentToDateUsd: 95,
          resets: [resetFixture()],
        }),
      }),
    })

    await spend.loadConsumption()

    expect(spend.hasResets.value).toBe(true)
    expect(spend.resetCount.value).toBe(1)
    expect(spend.meterCapUsd.value).toBe(200)
    // 95 of 200, not of the configured 100.
    expect(spend.meterPercent.value).toBe(47.5)
    expect(spend.remainingUsd.value).toBe(105)
    // The grant a further reset would give is reported, never derived by dividing the cap in force.
    expect(spend.configuredMeterCapUsd.value).toBe(100)
  })

  it('reports no resets for a period that was never reset', async () => {
    const spend = useClientSpendConsumption('c1', { loader: async () => ({ data: fixture() }) })

    await spend.loadConsumption()

    expect(spend.hasResets.value).toBe(false)
    expect(spend.resetCount.value).toBe(0)
  })

  it('offers the reset only on the current period', async () => {
    const spend = useClientSpendConsumption('c1', { loader: async () => ({ data: fixture() }) })

    await spend.loadConsumption()
    expect(spend.canReset.value).toBe(true)

    await spend.goToPreviousMonth()
    expect(spend.canReset.value).toBe(false)
  })

  it('does not offer the reset when no budget is configured', async () => {
    const spend = useClientSpendConsumption('c1', {
      loader: async () => ({ data: fixture({ monthlySoftCapUsd: null, monthlyHardCapUsd: null }) }),
    })

    await spend.loadConsumption()

    expect(spend.canReset.value).toBe(false)
  })

  it('reloads consumption and history after a successful reset', async () => {
    let loads = 0
    let historyLoads = 0
    let resets = 0
    const spend = useClientSpendConsumption('c1', {
      loader: async () => {
        loads += 1
        return { data: fixture() }
      },
      historyLoader: async () => {
        historyLoads += 1
        return { data: { clientId: 'c1', monthlySoftCapUsd: 80, monthlyHardCapUsd: 100, months: [] } }
      },
      resetAction: async () => {
        resets += 1
        return { data: resetFixture() }
      },
    })

    await spend.loadConsumption()
    const ok = await spend.performReset()

    expect(ok).toBe(true)
    expect(resets).toBe(1)
    // The reset reloads both views, so the meter and the trend agree on the raised cap.
    expect(loads).toBe(2)
    expect(historyLoads).toBe(1)
    expect(spend.resetError.value).toBe('')
  })

  it('surfaces an error and does not reload when the reset fails', async () => {
    let loads = 0
    const spend = useClientSpendConsumption('c1', {
      loader: async () => {
        loads += 1
        return { data: fixture() }
      },
      resetAction: async () => ({ error: 'boom' }),
    })

    await spend.loadConsumption()
    const ok = await spend.performReset()

    expect(ok).toBe(false)
    expect(loads).toBe(1)
    expect(spend.resetError.value).not.toBe('')
  })

  it('refuses to reset when the UTC month rolled over while the page was open', async () => {
    vi.useFakeTimers()
    try {
      vi.setSystemTime(new Date('2026-07-15T09:00:00Z'))
      const resetAction = vi.fn(async () => ({ data: resetFixture() }))
      const spend = useClientSpendConsumption('c1', {
        loader: async () => ({ data: fixture() }),
        resetAction,
      })
      await spend.loadConsumption()
      expect(spend.canReset.value).toBe(true)

      // The tab is still showing July, but the server would now reset August.
      vi.setSystemTime(new Date('2026-08-01T00:05:00Z'))
      const ok = await spend.performReset()

      expect(ok).toBe(false)
      expect(resetAction).not.toHaveBeenCalled()
      expect(spend.resetError.value).toContain('current month has changed')
    } finally {
      vi.useRealTimers()
    }
  })

  it('ignores a second reset while one is already in flight', async () => {
    let release: (() => void) | undefined
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })
    const resetAction = vi.fn(async () => {
      await gate
      return { data: resetFixture() }
    })
    const spend = useClientSpendConsumption('c1', {
      loader: async () => ({ data: fixture() }),
      resetAction,
    })
    await spend.loadConsumption()

    const first = spend.performReset()
    const second = await spend.performReset()
    release?.()
    await first

    expect(second).toBe(false)
    expect(resetAction).toHaveBeenCalledTimes(1)
  })

  it('clears a reset error when the picker moves to another period', async () => {
    const spend = useClientSpendConsumption('c1', {
      loader: async () => ({ data: fixture() }),
      resetAction: async () => ({ error: 'boom' }),
    })
    await spend.loadConsumption()
    await spend.performReset()
    expect(spend.resetError.value).not.toBe('')

    await spend.goToPreviousMonth()

    expect(spend.resetError.value).toBe('')
  })

  it('steps the history cap lines so a reset month shows its own ceiling', async () => {
    const spend = useClientSpendConsumption('c1', {
      historyLoader: async () => ({
        data: {
          clientId: 'c1',
          monthlySoftCapUsd: 80,
          monthlyHardCapUsd: 100,
          months: [
            {
              year: 2026,
              month: 6,
              periodStart: '2026-06-01',
              spentUsd: 55,
              spendIsApproximate: false,
              effectiveSoftCapUsd: 80,
              effectiveHardCapUsd: 100,
              resetCount: 0,
            },
            {
              year: 2026,
              month: 7,
              periodStart: '2026-07-01',
              spentUsd: 42,
              spendIsApproximate: false,
              effectiveSoftCapUsd: 160,
              effectiveHardCapUsd: 200,
              resetCount: 1,
            },
          ],
        },
      }),
    })

    await spend.loadHistory()

    const chart = spend.historyChartData.value
    const softCapLine = chart.datasets.find((set) => set.label === 'Soft cap')
    const hardCapLine = chart.datasets.find((set) => set.label === 'Hard cap')
    expect(softCapLine?.data).toEqual([80, 160])
    expect(hardCapLine?.data).toEqual([100, 200])
  })
})
