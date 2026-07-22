// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TenantBudgetOverviewView from '@/features/tenants/views/TenantBudgetOverviewView.vue'
import type { TenantBudgetOverview } from '@/services/tenantBudgetOverviewService'

const getTenantBudgetOverviewMock = vi.fn()
let capabilityAvailable = true

vi.mock('@/services/tenantBudgetOverviewService', () => ({
  getTenantBudgetOverview: (tenantId: string) => getTenantBudgetOverviewMock(tenantId),
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

function overview(): TenantBudgetOverview {
  return {
    tenantId: 't1',
    periodStart: '2026-07-01',
    periodEnd: '2026-07-31',
    asOf: '2026-07-15',
    clients: [
      { clientId: 'b', displayName: 'Globex', spentToDateUsd: 110, monthlySoftCapUsd: 80, monthlyHardCapUsd: 100, projectedPeriodSpendUsd: 130 },
      { clientId: 'a', displayName: 'Acme', spentToDateUsd: 30, monthlySoftCapUsd: 80, monthlyHardCapUsd: 100, projectedPeriodSpendUsd: 60 },
    ],
  }
}

function mountView() {
  return mount(TenantBudgetOverviewView, {
    global: { stubs: { BudgetMeter: { template: '<div class="budget-meter-stub" />' } } },
  })
}

describe('TenantBudgetOverviewView', () => {
  it('renders a filterable row per client with a drill-down link', async () => {
    capabilityAvailable = true
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })

    const wrapper = mountView()
    await flushPromises()

    expect(getTenantBudgetOverviewMock).toHaveBeenCalledWith('t1')
    expect(wrapper.findAll('.overview-row').length).toBe(2)
    expect(wrapper.find('.overview-search').exists()).toBe(true)
    expect(wrapper.text()).toContain('Globex')
    expect(wrapper.text()).toContain('Acme')
  })

  it('shows the upgrade message and does not load when budgeting is unavailable', async () => {
    capabilityAvailable = false
    getTenantBudgetOverviewMock.mockClear()

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Budgeting requires a commercial license.')
    expect(getTenantBudgetOverviewMock).not.toHaveBeenCalled()
  })
})
