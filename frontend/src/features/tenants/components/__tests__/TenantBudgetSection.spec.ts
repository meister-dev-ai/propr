// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import TenantBudgetSection from '@/features/tenants/components/TenantBudgetSection.vue'
import type { TenantBudgetOverview } from '@/services/tenantBudgetOverviewService'

const getTenantBudgetOverviewMock = vi.fn()
const resetClientBudgetSpendMock = vi.fn()
const getTenantMock = vi.fn()
const updateTenantMock = vi.fn()
// Reactive, because the section watches the capability. It is read from /auth/me after the mount, so a test
// that switches it on after mounting reproduces a cold load.
const capabilityAvailable = ref(true)
let tenantRole = 1

vi.mock('@/services/tenantBudgetOverviewService', () => ({
  getTenantBudgetOverview: (tenantId: string) => getTenantBudgetOverviewMock(tenantId),
}))

vi.mock('@/services/budgetConsumptionService', () => ({
  resetClientBudgetSpend: (clientId: string) => resetClientBudgetSpendMock(clientId),
}))

vi.mock('@/services/tenantAdminService', () => ({
  getTenant: (tenantId: string) => getTenantMock(tenantId),
  updateTenant: (tenantId: string, request: unknown) => updateTenantMock(tenantId, request),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isCapabilityAvailable: () => capabilityAvailable.value,
    getCapability: () => ({ isAvailable: capabilityAvailable.value, message: 'Budgeting requires a commercial license.' }),
    hasTenantRole: (_tenantId: string, minRole: number) => tenantRole >= minRole,
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

function tenant(softCapUsd: number | null = null, hardCapUsd: number | null = null, isEditable = true) {
  return {
    id: 't1',
    slug: 'acme',
    displayName: 'Acme',
    isActive: true,
    localLoginEnabled: true,
    isEditable,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    budget: { monthlySoftCapUsd: softCapUsd, monthlyHardCapUsd: hardCapUsd },
  }
}

/** The sections this file has mounted, unmounted after every test so none of them outlives its test. */
const mountedWrappers: { unmount: () => void }[] = []

function mountView() {
  const wrapper = mount(TenantBudgetSection, {
    global: { stubs: { BudgetMeter: { template: '<div class="budget-meter-stub" />' } } },
  })
  mountedWrappers.push(wrapper)
  return wrapper
}

// The capability is shared, reactive module state and every mounted section watches it. A section left behind
// reads the tenant again when the next test switches the capability on.
afterEach(() => {
  while (mountedWrappers.length > 0) {
    mountedWrappers.pop()!.unmount()
  }
})

describe('TenantBudgetSection', () => {
  beforeEach(() => {
    // The capability flag and the tenant role are module-level state shared by every test in this file, so a
    // test that leaves one switched off must not decide what the next one renders.
    capabilityAvailable.value = true
    tenantRole = 1
    getTenantBudgetOverviewMock.mockReset()
    resetClientBudgetSpendMock.mockReset()
    getTenantMock.mockReset()
    updateTenantMock.mockReset()
    getTenantMock.mockResolvedValue(tenant())
    updateTenantMock.mockImplementation((_id: string, request: { budget: { monthlySoftCapUsd: number | null; monthlyHardCapUsd: number | null } }) =>
      Promise.resolve(tenant(request.budget.monthlySoftCapUsd, request.budget.monthlyHardCapUsd)),
    )
  })

  it('renders a filterable row per client with a drill-down link', async () => {
    capabilityAvailable.value = true
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
    capabilityAvailable.value = false
    getTenantBudgetOverviewMock.mockClear()

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Budgeting requires a commercial license.')
    expect(getTenantBudgetOverviewMock).not.toHaveBeenCalled()
  })

  it('resets one client from its row after confirmation, then reloads', async () => {
    capabilityAvailable.value = true
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
    capabilityAvailable.value = true
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
    capabilityAvailable.value = true
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

  it('shows the stored tenant caps and saves an edit through the tenant patch', async () => {
    capabilityAvailable.value = true
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000))

    const wrapper = mountView()
    await flushPromises()

    const soft = wrapper.find('[data-testid="tenant-soft-cap"]')
    expect((soft.element as HTMLInputElement).value).toBe('4000')
    expect((wrapper.find('[data-testid="tenant-hard-cap"]').element as HTMLInputElement).value).toBe('5000')

    await soft.setValue('4500')
    await wrapper.find('[data-testid="tenant-caps-save"]').trigger('click')
    await flushPromises()

    expect(updateTenantMock).toHaveBeenCalledWith('t1', {
      budget: { monthlySoftCapUsd: 4500, monthlyHardCapUsd: 5000 },
    })
  })

  it('clears a cap when its field is emptied', async () => {
    capabilityAvailable.value = true
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000))

    const wrapper = mountView()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-soft-cap"]').setValue('')
    await wrapper.find('[data-testid="tenant-caps-save"]').trigger('click')
    await flushPromises()

    expect(updateTenantMock).toHaveBeenCalledWith('t1', {
      budget: { monthlySoftCapUsd: null, monthlyHardCapUsd: 5000 },
    })
  })

  it('reads the caps without a form for a tenant user who cannot change them', async () => {
    tenantRole = 0
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000))

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-soft-cap"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-caps-save"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-soft-cap-readout"]').text()).toContain('4,000')
    expect(wrapper.find('[data-testid="tenant-caps-readonly"]').text()).toContain('tenant administrator')
  })

  it('reads the caps without a form for a tenant that cannot be changed', async () => {
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000, false))

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-soft-cap"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-caps-readonly"]').text()).toContain('System tenant')
  })

  it('refuses to save a soft cap above the hard cap', async () => {
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000))

    const wrapper = mountView()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-soft-cap"]').setValue('6000')

    expect(wrapper.find('[data-testid="tenant-caps-validation"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="tenant-caps-save"]').attributes('disabled')).toBeDefined()
    expect(updateTenantMock).not.toHaveBeenCalled()
  })

  // A failed first load leaves no stored caps. The edit form would show two blank fields, which read as "no
  // limit" and would clear both caps if saved, so the section reports the failure and offers the load again.
  it('offers the load again instead of a blank cap form when the tenant could not be read', async () => {
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    // One response per expected read: a read this test did not ask for fails instead of quietly succeeding.
    getTenantMock
      .mockImplementationOnce(() => Promise.reject(new Error('boom')))
      .mockImplementationOnce(() => Promise.resolve(tenant(4000, 5000)))
      .mockImplementation(() => Promise.reject(new Error('the tenant was read more often than this test allows')))

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-soft-cap"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-soft-cap-readout"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="tenant-caps-error"]').text()).toContain('Failed to load')
    expect(wrapper.find('[data-testid="tenant-caps-unavailable"]').exists()).toBe(true)

    await wrapper.find('[data-testid="tenant-caps-retry"]').trigger('click')
    await flushPromises()

    expect(getTenantMock).toHaveBeenCalledTimes(2)
    expect((wrapper.find('[data-testid="tenant-soft-cap"]').element as HTMLInputElement).value).toBe('4000')
  })

  it('offers no cap form and reads no tenant when budgeting is unavailable', async () => {
    capabilityAvailable.value = false

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="tenant-soft-cap"]').exists()).toBe(false)
    expect(getTenantMock).not.toHaveBeenCalled()
  })

  // On a cold load the capabilities are still on their way from /auth/me when this section mounts. Without the
  // watcher neither card is read: the caps section offers the load again for a load it never attempted, and
  // the overview stays empty until the page is reloaded.
  it('reads the tenant caps once budgeting turns available after the mount', async () => {
    capabilityAvailable.value = false
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000))

    const wrapper = mountView()
    await flushPromises()
    expect(getTenantMock).not.toHaveBeenCalled()

    capabilityAvailable.value = true
    await flushPromises()

    expect(getTenantMock).toHaveBeenCalledTimes(1)
    expect((wrapper.find('[data-testid="tenant-soft-cap"]').element as HTMLInputElement).value).toBe('4000')
    expect(wrapper.find('[data-testid="tenant-caps-unavailable"]').exists()).toBe(false)
  })

  it('reads the budget overview once budgeting turns available after the mount', async () => {
    capabilityAvailable.value = false
    getTenantBudgetOverviewMock.mockResolvedValue({ data: overview() })
    getTenantMock.mockResolvedValue(tenant(4000, 5000))

    const wrapper = mountView()
    await flushPromises()
    expect(getTenantBudgetOverviewMock).not.toHaveBeenCalled()

    capabilityAvailable.value = true
    await flushPromises()

    expect(getTenantBudgetOverviewMock).toHaveBeenCalledTimes(1)
    expect(wrapper.findAll('.overview-row').length).toBe(2)
  })
})
