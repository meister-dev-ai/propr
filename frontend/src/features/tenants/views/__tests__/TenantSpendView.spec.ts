// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TenantSpendView from '@/features/tenants/views/TenantSpendView.vue'
import type { TenantSpend } from '@/services/tenantBudgetOverviewService'

const getTenantBudgetSpendMock = vi.fn()
let capabilityAvailable = true

vi.mock('@/services/tenantBudgetOverviewService', () => ({
  getTenantBudgetSpend: (tenantId: string, months: number) => getTenantBudgetSpendMock(tenantId, months),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isCapabilityAvailable: () => capabilityAvailable,
    getCapability: () => ({ isAvailable: capabilityAvailable, message: 'Budgeting requires a commercial license.' }),
  }),
}))

vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { tenantId: 't1' } }),
  RouterLink: { props: ['to'], template: '<a class="router-link"><slot /></a>' },
}))

vi.mock('vue-chartjs', () => ({
  Line: { name: 'LineChartStub', template: '<div class="chart-stub" />' },
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

function spend(): TenantSpend {
  return {
    tenantId: 't1',
    periodStart: '2026-07-01',
    periodEnd: '2026-07-31',
    asOf: '2026-07-15',
    spentToDateUsd: 90,
    monthlySoftCapUsd: 120,
    monthlyHardCapUsd: 150,
    projectedPeriodSpendUsd: 110,
    months: [
      { year: 2026, month: 6, periodStart: '2026-06-01', spentUsd: 100 },
      { year: 2026, month: 7, periodStart: '2026-07-01', spentUsd: 90 },
    ],
  }
}

function mountView() {
  return mount(TenantSpendView, {
    global: {
      stubs: {
        BudgetMeter: { template: '<div class="budget-meter-stub" />' },
      },
    },
  })
}

describe('TenantSpendView', () => {
  it('renders aggregate spend, summed caps, and the trend chart', async () => {
    capabilityAvailable = true
    getTenantBudgetSpendMock.mockResolvedValue({ data: spend() })

    const wrapper = mountView()
    await flushPromises()

    expect(getTenantBudgetSpendMock).toHaveBeenCalledWith('t1', 12)
    expect(wrapper.text()).toContain('Spent to date')
    expect(wrapper.text()).toContain('Summed soft cap')
    expect(wrapper.find('.budget-meter-stub').exists()).toBe(true)
    expect(wrapper.find('.chart-stub').exists()).toBe(true)
  })

  it('shows the upgrade message and does not load when budgeting is unavailable', async () => {
    capabilityAvailable = false
    getTenantBudgetSpendMock.mockClear()

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Budgeting requires a commercial license.')
    expect(getTenantBudgetSpendMock).not.toHaveBeenCalled()
  })
})
