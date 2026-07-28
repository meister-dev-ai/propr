// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TenantBudgetSection from '@/features/tenants/components/TenantBudgetSection.vue'
import type { TenantBudgetOverview } from '@/services/tenantBudgetOverviewService'

const getTenantBudgetOverviewMock = vi.fn()
const resetClientBudgetSpendMock = vi.fn()
let capabilityAvailable = true

vi.mock('@/services/tenantBudgetOverviewService', () => ({
  getTenantBudgetOverview: (tenantId: string) => getTenantBudgetOverviewMock(tenantId),
}))

vi.mock('@/services/budgetConsumptionService', () => ({
  resetClientBudgetSpend: (clientId: string) => resetClientBudgetSpendMock(clientId),
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
  return mount(TenantBudgetSection, {
    global: { stubs: { BudgetMeter: { template: '<div class="budget-meter-stub" />' } } },
  })
}

describe('TenantBudgetSection', () => {
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

  it('resets one client from its row after confirmation, then reloads', async () => {
    capabilityAvailable = true
    getTenantBudgetOverviewMock.mockClear()
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    resetClientBudgetSpendMock.mockClear()
    resetClientBudgetSpendMock.mockResolvedValue({ data: { id: 'r1', periodStart: '2026-07-01' } })

    const wrapper = mountView()
    await flushPromises()

    // Globex sorts first (highest spend), so its row action targets that client.
    await wrapper.findAll('[data-testid="reset-spend-button"]')[0].trigger('click')
    const dialog = wrapper.find('.confirm-dialog')
    expect(dialog.text()).toContain('Globex')
    expect(resetClientBudgetSpendMock).not.toHaveBeenCalled()

    await dialog.find('.btn-danger').trigger('click')
    await flushPromises()

    expect(resetClientBudgetSpendMock).toHaveBeenCalledWith('b')
    expect(getTenantBudgetOverviewMock).toHaveBeenCalledTimes(2)
  })

  it('marks a row that was reset this period', async () => {
    capabilityAvailable = true
    const data = overview()
    data.clients = [
      {
        clientId: 'a',
        displayName: 'Acme',
        spentToDateUsd: 95,
        monthlySoftCapUsd: 160,
        monthlyHardCapUsd: 200,
        projectedPeriodSpendUsd: 190,
        resetCount: 2,
      },
    ]
    getTenantBudgetOverviewMock.mockResolvedValue({ data })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="reset-marker"]').text()).toContain('Reset ×2')
    // The row quotes the cap in force, not the configured baseline.
    expect(wrapper.find('.overview-amount').text()).toContain('$200.00')
  })

  it('offers no reset action for a client without a budget', async () => {
    capabilityAvailable = true
    const data = overview()
    data.clients = [
      {
        clientId: 'c',
        displayName: 'Umbrella',
        spentToDateUsd: 20,
        monthlySoftCapUsd: null,
        monthlyHardCapUsd: null,
        projectedPeriodSpendUsd: 40,
      },
    ]
    getTenantBudgetOverviewMock.mockResolvedValue({ data })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="reset-spend-button"]').exists()).toBe(false)
  })
})
