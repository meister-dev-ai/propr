// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  getElevationStatus: vi.fn(),
  startElevation: vi.fn(),
  endElevation: vi.fn(),
  listTenantClients: vi.fn(),
}))

const multiTenancy = ref(true)

vi.mock('@/services/elevationsService', () => mocks)
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => true),
    isAuthenticated: computed(() => true),
    isCapabilityAvailable: (key: string) => key === 'multi-tenancy' && multiTenancy.value,
  }),
}))

enableAutoUnmount(afterEach)

function elevation() {
  return {
    id: 'elevation-1',
    administratorName: 'operator',
    tenantId: 'tenant-1',
    tenantDisplayName: 'Acme',
    clientId: null,
    clientDisplayName: null,
    reason: 'SUP-1042',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
    endedAt: null,
    endedByName: null,
    state: 'active',
    contentReads: 0,
  }
}

async function mountPanel(props: Record<string, unknown> = {}) {
  const { usePlatformAdminElevations } = await import('@/composables/usePlatformAdminElevations')
  usePlatformAdminElevations().reset()
  const { default: Panel } = await import('../PlatformAdminElevationPanel.vue')
  return mount(Panel, {
    props: { tenantId: 'tenant-1', tenantName: 'Acme', ...props },
    global: { stubs: { Teleport: true } },
  })
}

describe('PlatformAdminElevationPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    multiTenancy.value = true
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 240, active: [] })
    mocks.listTenantClients.mockResolvedValue([{ id: 'client-1', displayName: 'Payments' }])
  })

  it('says that the active elevations could not be loaded, offers no start and loads them again on request', async () => {
    mocks.getElevationStatus
      .mockRejectedValueOnce(new Error('The active elevations could not be loaded.'))
      .mockResolvedValueOnce({ maxDurationMinutes: 240, active: [elevation()] })
    const wrapper = await mountPanel()
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-panel-load-error"]').text())
      .toContain('Whether an elevation covers this tenant is not known.')
    expect(wrapper.find('[data-testid="elevation-panel-start"]').exists()).toBe(false)

    await wrapper.get('[data-testid="elevation-panel-retry"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="elevation-panel-load-error"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('You are elevated for every client of Acme')
  })

  it('offers the start only once the clients of the tenant are loaded, and loads a failed list again on request', async () => {
    mocks.listTenantClients
      .mockRejectedValueOnce(new Error('The clients of the tenant could not be loaded.'))
      .mockResolvedValueOnce([{ id: 'client-1', displayName: 'Payments' }])
    const wrapper = await mountPanel()
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-panel-clients-error"]').text())
      .toContain('The clients of the tenant could not be loaded.')
    expect(wrapper.find('[data-testid="elevation-panel-start"]').exists()).toBe(false)

    await wrapper.get('[data-testid="elevation-panel-clients-retry"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="elevation-panel-start"]').trigger('click')

    const scopes = wrapper.findAll('[data-testid="elevation-scope"] option').map((option) => option.text())
    expect(scopes).toEqual(['Every client of Acme', 'Client Payments'])
  })

  it('shows nothing and sends no request when the license does not include multi-tenancy', async () => {
    multiTenancy.value = false
    const wrapper = await mountPanel()
    await flushPromises()

    expect(wrapper.find('[data-testid="elevation-panel"]').exists()).toBe(false)
    expect(mocks.getElevationStatus).not.toHaveBeenCalled()
    expect(mocks.listTenantClients).not.toHaveBeenCalled()
  })

  it('needs no client list on the page of a client', async () => {
    const wrapper = await mountPanel({ clientId: 'client-1', clientName: 'Payments' })
    await flushPromises()

    expect(mocks.listTenantClients).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="elevation-panel-start"]').attributes('disabled')).toBeUndefined()
  })
})
