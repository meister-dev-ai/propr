// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { computed } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import ClientSpendTab from '@/features/clients/components/ClientSpendTab.vue'
import { ClientDetailVmKey } from '@/features/clients/view-models/useClientDetailViewModel'
import type { ClientBudgetConsumption } from '@/services/budgetConsumptionService'

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
    ...overrides,
  }
}

function makeVm(opts: { available?: boolean; message?: string } = {}) {
  return {
    clientId: 'c1',
    isBudgetingAvailable: computed(() => opts.available ?? true),
    budgetingUpgradeMessage: computed(() => opts.message ?? ''),
  }
}

function mountTab(vm: ReturnType<typeof makeVm>, loader: (clientId: string) => Promise<{ data?: ClientBudgetConsumption | null; error?: unknown }>) {
  return mount(ClientSpendTab, {
    props: { loader },
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
    expect(wrapper.find('.meter').exists()).toBe(true)
    // Projected 130 exceeds the hard cap 100.
    expect(text).toContain('hard cap')
  })

  it('renders the no-budget state when no caps are configured', async () => {
    const wrapper = mountTab(
      makeVm(),
      async () => ({ data: consumption({ monthlySoftCapUsd: null, monthlyHardCapUsd: null }) }),
    )
    await flushPromises()

    expect(wrapper.text()).toContain('No monthly budget configured')
    expect(wrapper.find('.meter').exists()).toBe(false)
  })

  it('shows an error state with a retry affordance when the load fails', async () => {
    const wrapper = mountTab(makeVm(), async () => ({ error: 'boom' }))
    await flushPromises()

    expect(wrapper.find('.error').exists()).toBe(true)
    expect(wrapper.text()).toContain('Try Again')
  })
})
