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
}))

const isAdmin = ref(true)
const multiTenancy = ref(true)

vi.mock('@/services/elevationsService', () => mocks)
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => isAdmin.value),
    isAuthenticated: computed(() => true),
    isCapabilityAvailable: (key: string) => key === 'multi-tenancy' && multiTenancy.value,
  }),
}))

function elevation(overrides: Record<string, unknown> = {}) {
  return {
    id: 'elevation-1',
    administratorName: 'operator',
    tenantId: 'tenant-1',
    tenantDisplayName: 'Acme',
    clientId: 'client-1',
    clientDisplayName: 'Payments',
    reason: 'SUP-1042',
    startedAt: new Date(Date.now() - 60_000).toISOString(),
    expiresAt: new Date(Date.now() + 90 * 60_000).toISOString(),
    state: 'active',
    contentReads: 0,
    ...overrides,
  }
}

async function mountBanner() {
  const { default: Banner } = await import('../PlatformAdminElevationBanner.vue')
  return mount(Banner, {
    global: { stubs: { RouterLink: { props: ['to'], template: '<a><slot /></a>' } } },
  })
}

enableAutoUnmount(afterEach)

describe('PlatformAdminElevationBanner', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    isAdmin.value = true
    multiTenancy.value = true
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 240, active: [elevation()] })
    mocks.endElevation.mockResolvedValue(elevation({ state: 'ended' }))
  })

  afterEach(() => {
    vi.useRealTimers()
  })

  it('names the scope, the reason and the remaining time of an active elevation', async () => {
    const wrapper = await mountBanner()
    await flushPromises()

    const banner = wrapper.get('[data-testid="elevation-banner"]')
    expect(banner.text()).toContain('client Payments of Acme')
    expect(banner.text()).toContain('SUP-1042')
    expect(wrapper.get('[data-testid="elevation-remaining"]').text()).toMatch(/^1 h (29|30) min$/)
  })

  it('ends the elevation and hides the banner once no elevation is active', async () => {
    const wrapper = await mountBanner()
    await flushPromises()
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 240, active: [] })

    await wrapper.get('[data-testid="elevation-end"]').trigger('click')
    await flushPromises()

    expect(mocks.endElevation).toHaveBeenCalledWith('elevation-1')
    expect(wrapper.find('[data-testid="elevation-banner"]').exists()).toBe(false)
  })

  it('says that the active elevations could not be loaded and loads them again on request', async () => {
    mocks.getElevationStatus
      .mockRejectedValueOnce(new Error('The active elevations could not be loaded.'))
      .mockResolvedValueOnce({ maxDurationMinutes: 240, active: [elevation()] })
    const wrapper = await mountBanner()
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-banner-load-error"]').text())
      .toContain('Whether you have an active elevation is not known.')

    await wrapper.get('[data-testid="elevation-banner-retry"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="elevation-banner-load-error"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="elevation-banner"]').text()).toContain('client Payments of Acme')
  })

  it('shows nothing to a caller who is not a platform administrator', async () => {
    isAdmin.value = false
    const wrapper = await mountBanner()
    await flushPromises()

    expect(mocks.getElevationStatus).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="elevation-banner"]').exists()).toBe(false)
  })

  it('shows nothing and requests no elevation status when the license does not include multi-tenancy', async () => {
    multiTenancy.value = false
    const wrapper = await mountBanner()
    await flushPromises()

    expect(mocks.getElevationStatus).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="elevation-banner"]').exists()).toBe(false)
  })

  it('keeps no elevation that a load reports after the banner was removed, for example at the sign-out', async () => {
    let answer!: (value: unknown) => void
    mocks.getElevationStatus.mockReturnValue(new Promise((resolve) => {
      answer = resolve
    }))
    const wrapper = await mountBanner()

    wrapper.unmount()
    answer({ maxDurationMinutes: 240, active: [elevation()] })
    await flushPromises()

    const { usePlatformAdminElevations } = await import('@/composables/usePlatformAdminElevations')
    expect(usePlatformAdminElevations().active.value).toEqual([])
  })

  it('drops an elevation from the banner once its expiry has passed', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: false })
    vi.setSystemTime(new Date('2026-10-26T10:00:00Z'))
    mocks.getElevationStatus.mockResolvedValue({
      maxDurationMinutes: 240,
      active: [elevation({ expiresAt: '2026-10-26T10:00:05Z' })],
    })
    const wrapper = await mountBanner()
    await flushPromises()
    expect(wrapper.find('[data-testid="elevation-banner"]').exists()).toBe(true)

    await vi.advanceTimersByTimeAsync(6000)

    expect(wrapper.find('[data-testid="elevation-banner"]').exists()).toBe(false)
  })
})
