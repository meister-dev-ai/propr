// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { computed } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import ClientSpendTab from '@/features/clients/components/ClientSpendTab.vue'
import { ClientDetailVmKey } from '@/features/clients/view-models/useClientDetailViewModel'
import type {
  BudgetSpendReset,
  ClientBudgetConsumption,
  ClientBudgetHistory,
} from '@/services/budgetConsumptionService'

vi.mock('vue-chartjs', () => ({
  Line: { name: 'LineChartStub', template: '<div class="line-chart-stub" />' },
}))

vi.mock('chart.js', () => ({
  Chart: { register: () => {} },
  CategoryScale: {},
  LinearScale: {},
  PointElement: {},
  LineElement: {},
  Title: {},
  Tooltip: {},
  Legend: {},
  Filler: {},
}))

function consumption(overrides: Partial<ClientBudgetConsumption> = {}): ClientBudgetConsumption {
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
    dailySpend: [{ date: '2026-07-10', spentUsd: 42 }],
    resets: [],
    configuredSoftCapUsd: 80,
    configuredHardCapUsd: 100,
    ...overrides,
  }
}

function history(): ClientBudgetHistory {
  return {
    clientId: 'c1',
    monthlySoftCapUsd: 80,
    monthlyHardCapUsd: 100,
    months: [
      { year: 2026, month: 6, periodStart: '2026-06-01', spentUsd: 55, spendIsApproximate: false },
      { year: 2026, month: 7, periodStart: '2026-07-01', spentUsd: 42, spendIsApproximate: false },
    ],
  }
}

function makeVm(opts: { available?: boolean; message?: string } = {}) {
  return {
    clientId: 'c1',
    isBudgetingAvailable: computed(() => opts.available ?? true),
    budgetingUpgradeMessage: computed(() => opts.message ?? ''),
  }
}

type ConsumptionLoader = (clientId: string, period?: string) => Promise<{ data?: ClientBudgetConsumption | null; error?: unknown }>

type ResetAction = (clientId: string) => Promise<{ data?: BudgetSpendReset | null; error?: unknown }>

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

function mountTab(vm: ReturnType<typeof makeVm>, loader: ConsumptionLoader, resetAction?: ResetAction) {
  return mount(ClientSpendTab, {
    props: {
      loader,
      historyLoader: async () => ({ data: history() }),
      resetAction,
    },
    global: {
      provide: { [ClientDetailVmKey as symbol]: vm },
      stubs: { ProgressOrb: { template: '<div class="orb-stub" />' } },
    },
  })
}

describe('ClientSpendTab', () => {
  it('shows the upgrade message and does not load when budgeting is unavailable', async () => {
    const loader = vi.fn()
    const wrapper = mountTab(
      makeVm({ available: false, message: 'Budgeting requires a commercial license.' }),
      loader,
    )
    await flushPromises()

    expect(wrapper.text()).toContain('Budgeting requires a commercial license.')
    expect(loader).not.toHaveBeenCalled()
  })

  it('renders spend, caps, the meter, and a forecast warning when over-projected', async () => {
    const wrapper = mountTab(makeVm(), async () => ({ data: consumption() }))
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('$42.00')
    expect(text).toContain('$80.00')
    expect(text).toContain('$100.00')
    expect(wrapper.find('.budget-meter').exists()).toBe(true)
    // Projected 130 exceeds the hard cap 100.
    expect(text).toContain('hard cap')
  })

  it('renders the period picker and the 12-month history chart', async () => {
    const wrapper = mountTab(makeVm(), async () => ({ data: consumption() }))
    await flushPromises()

    expect(wrapper.find('.period-picker').exists()).toBe(true)
    expect(wrapper.text()).toContain('Last 12 months')
    // Two Line charts: the current-period cumulative chart and the 12-month history chart.
    expect(wrapper.findAll('.line-chart-stub').length).toBe(2)
  })

  it('reloads consumption for the selected month when the picker steps back', async () => {
    const loader = vi.fn(async (_clientId: string, _period?: string) => ({ data: consumption() }))
    const wrapper = mountTab(makeVm(), loader)
    await flushPromises()
    loader.mockClear()

    await wrapper.find('.period-picker .period-nav').trigger('click')
    await flushPromises()

    expect(loader).toHaveBeenCalledTimes(1)
    const period = loader.mock.calls[0][1]
    expect(period).toMatch(/^\d{4}-\d{2}$/)
  })

  it('renders the no-budget state when no caps are configured', async () => {
    const wrapper = mountTab(
      makeVm(),
      async () => ({ data: consumption({ monthlySoftCapUsd: null, monthlyHardCapUsd: null }) }),
    )
    await flushPromises()

    expect(wrapper.text()).toContain('No monthly budget configured')
    expect(wrapper.find('.budget-meter').exists()).toBe(false)
  })

  it('shows an error state with a retry affordance when the load fails', async () => {
    const wrapper = mountTab(makeVm(), async () => ({ error: 'boom' }))
    await flushPromises()

    expect(wrapper.find('.error').exists()).toBe(true)
    expect(wrapper.text()).toContain('Try Again')
  })

  it('offers the reset action on the current period and confirms before granting', async () => {
    const resetAction = vi.fn(async () => ({ data: resetFixture() }))
    const wrapper = mountTab(makeVm(), async () => ({ data: consumption() }), resetAction)
    await flushPromises()

    const button = wrapper.find('[data-testid="reset-spend-button"]')
    expect(button.exists()).toBe(true)

    await button.trigger('click')
    // The prompt names the grant and the resulting ceiling so the jump is a decision, not a surprise.
    const dialog = wrapper.find('.confirm-dialog')
    expect(dialog.exists()).toBe(true)
    expect(dialog.text()).toContain('$100.00')
    expect(dialog.text()).toContain('$200.00')
    // Nothing is granted until the confirmation is accepted.
    expect(resetAction).not.toHaveBeenCalled()

    await dialog.find('.btn-danger').trigger('click')
    await flushPromises()

    expect(resetAction).toHaveBeenCalledTimes(1)
  })

  it('quotes the grant against the already-topped-up ceiling, not a doubling of it', async () => {
    // Configured $100 with $200 already in force must read "grant $100 → $300"; a formula that doubled either
    // figure would read $200 or $400.
    const wrapper = mountTab(
      makeVm(),
      async () => ({
        data: consumption({
          monthlySoftCapUsd: 160,
          monthlyHardCapUsd: 200,
          configuredSoftCapUsd: 80,
          configuredHardCapUsd: 100,
          resets: [resetFixture()],
        }),
      }),
      async () => ({ data: resetFixture() }),
    )
    await flushPromises()

    await wrapper.find('[data-testid="reset-spend-button"]').trigger('click')

    const dialog = wrapper.find('.confirm-dialog')
    expect(dialog.text()).toContain('fresh $100.00')
    expect(dialog.text()).toContain('$200.00 to $300.00')
  })

  it('does not grant anything when the confirmation is cancelled', async () => {
    const resetAction = vi.fn(async () => ({ data: resetFixture() }))
    const wrapper = mountTab(makeVm(), async () => ({ data: consumption() }), resetAction)
    await flushPromises()

    await wrapper.find('[data-testid="reset-spend-button"]').trigger('click')
    await wrapper.find('.confirm-dialog button:not(.btn-danger)').trigger('click')
    await flushPromises()

    expect(resetAction).not.toHaveBeenCalled()
    expect(wrapper.find('.confirm-dialog').exists()).toBe(false)
  })

  it('marks a reset period and lists each reset with its actor and before/after cap', async () => {
    const wrapper = mountTab(
      makeVm(),
      async () => ({
        data: consumption({
          monthlySoftCapUsd: 160,
          monthlyHardCapUsd: 200,
          configuredSoftCapUsd: 80,
          configuredHardCapUsd: 100,
          spentToDateUsd: 95,
          resets: [resetFixture()],
        }),
      }),
    )
    await flushPromises()

    expect(wrapper.find('[data-testid="reset-marker"]').text()).toContain('Reset ×1')
    const details = wrapper.find('[data-testid="reset-details"]')
    expect(details.exists()).toBe(true)
    expect(details.text()).toContain('saen')
    expect(details.text()).toContain('$100.00')
    expect(details.text()).toContain('$200.00')
    // The meter caption reads against the cumulative cap.
    expect(wrapper.find('.meter-caption').text()).toContain('$200.00')
  })

  it('reads each audit line from the scope the reset recorded, not from today config', async () => {
    // The reset was granted when only a soft cap existed; a hard cap was configured afterwards. Reading today's
    // config would render the entry as "No limit → No limit".
    const wrapper = mountTab(
      makeVm(),
      async () => ({
        data: consumption({
          monthlySoftCapUsd: 160,
          monthlyHardCapUsd: 100,
          configuredSoftCapUsd: 80,
          configuredHardCapUsd: 100,
          resets: [resetFixture({
            topUpHardCapUsd: null,
            effectiveHardCapBeforeUsd: null,
            effectiveHardCapAfterUsd: null,
          })],
        }),
      }),
    )
    await flushPromises()

    const details = wrapper.find('[data-testid="reset-details"]')
    expect(details.text()).toContain('$80.00')
    expect(details.text()).toContain('$160.00')
    expect(details.text()).not.toContain('No limit')
  })

  it('hides the reset action when no budget is configured', async () => {
    const wrapper = mountTab(
      makeVm(),
      async () => ({ data: consumption({ monthlySoftCapUsd: null, monthlyHardCapUsd: null }) }),
    )
    await flushPromises()

    expect(wrapper.find('[data-testid="reset-spend-button"]').exists()).toBe(false)
  })

  it('hides the reset action on a past period', async () => {
    const wrapper = mountTab(makeVm(), async () => ({ data: consumption() }))
    await flushPromises()

    await wrapper.find('.period-picker .period-nav').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="reset-spend-button"]').exists()).toBe(false)
  })
})
