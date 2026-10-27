// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  getClientNames: vi.fn(),
  getElevationStatus: vi.fn(),
  startElevation: vi.fn(),
  endElevation: vi.fn(),
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

async function mountNotice(requirement = { tenantId: 'tenant-1', clientId: 'client-1' }) {
  const { default: Notice } = await import('../ElevationRequiredNotice.vue')
  return mount(Notice, {
    props: { requirement, subject: 'the protocol of this review' },
    global: { stubs: { Teleport: true } },
  })
}

describe('ElevationRequiredNotice', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    multiTenancy.value = true
    mocks.getClientNames.mockResolvedValue({ clientName: 'Payments', tenantName: 'Acme' })
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 240, active: [] })
    mocks.startElevation.mockResolvedValue({
      id: 'elevation-1',
      tenantId: 'tenant-1',
      clientId: 'client-1',
      expiresAt: new Date(Date.now() + 60 * 60_000).toISOString(),
    })
  })

  it('names the client and the tenant the content belongs to', async () => {
    const wrapper = await mountNotice()
    await flushPromises()

    const text = wrapper.get('[data-testid="elevation-required"]').text()
    expect(text).toContain('The protocol of this review belongs to client Payments of the tenant Acme')
    expect(text).toContain('audit history of the tenant')
  })

  it('names the client of the current refusal when the lookup for the previous client finishes later', async () => {
    let answerPrevious!: (value: { clientName: string; tenantName: string }) => void
    mocks.getClientNames
      .mockReturnValueOnce(new Promise((resolve) => {
        answerPrevious = resolve
      }))
      .mockResolvedValueOnce({ clientName: 'Ledger', tenantName: 'Globex' })
    const wrapper = await mountNotice()

    await wrapper.setProps({ requirement: { tenantId: 'tenant-2', clientId: 'client-2' } })
    await flushPromises()
    answerPrevious({ clientName: 'Payments', tenantName: 'Acme' })
    await flushPromises()

    const text = wrapper.get('[data-testid="elevation-required"]').text()
    expect(text).toContain('client Ledger of the tenant Globex')
    expect(text).not.toContain('Payments')
  })

  it('offers no start and looks up no names when the license does not include multi-tenancy', async () => {
    multiTenancy.value = false
    const wrapper = await mountNotice()
    await flushPromises()

    expect(wrapper.find('[data-testid="elevation-required-start"]').exists()).toBe(false)
    expect(mocks.getClientNames).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="elevation-required-unlicensed"]').text()).toContain('multi-tenancy')
  })

  it('starts an elevation for the client and reports it, so the page can load the content', async () => {
    const wrapper = await mountNotice()
    await flushPromises()

    await wrapper.get('[data-testid="elevation-required-start"]').trigger('click')
    await wrapper.get('[data-testid="elevation-reason"]').setValue('SUP-1042')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(mocks.startElevation).toHaveBeenCalledWith(expect.objectContaining({ tenantId: 'tenant-1', clientId: 'client-1' }))
    expect(wrapper.emitted('started')).toEqual([
      [expect.objectContaining({ id: 'elevation-1', tenantId: 'tenant-1', clientId: 'client-1' })],
    ])
  })
})
