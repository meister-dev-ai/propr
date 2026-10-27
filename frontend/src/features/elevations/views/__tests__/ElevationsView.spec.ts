// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { flushPromises, mount } from '@vue/test-utils'
import { computed } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  listElevations: vi.fn(),
  listElevationAuditEntries: vi.fn(),
  getElevationStatus: vi.fn(),
  startElevation: vi.fn(),
  endElevation: vi.fn(),
}))

vi.mock('@/services/elevationsService', () => mocks)
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isAdmin: computed(() => true),
    isAuthenticated: computed(() => true),
    isCapabilityAvailable: (key: string) => key === 'multi-tenancy',
  }),
}))

function elevation(overrides: Record<string, unknown> = {}) {
  return {
    id: 'elevation-1',
    administratorName: 'operator',
    tenantId: 'tenant-1',
    tenantDisplayName: 'Acme',
    clientId: null,
    clientDisplayName: null,
    reason: 'SUP-1042',
    startedAt: '2026-10-26T09:00:00Z',
    expiresAt: '2026-10-26T13:00:00Z',
    endedAt: null,
    endedByName: null,
    state: 'active',
    contentReads: 3,
    ...overrides,
  }
}

async function mountView() {
  const { default: ElevationsView } = await import('../ElevationsView.vue')
  return mount(ElevationsView, {
    global: {
      stubs: {
        RouterLink: { props: ['to'], template: '<a><slot /></a>' },
        AppTopBar: { template: '<div><slot /></div>' },
      },
    },
  })
}

describe('ElevationsView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listElevations.mockResolvedValue({
      items: [
        elevation(),
        elevation({ id: 'elevation-2', administratorName: 'second-operator', state: 'ended', endedByName: 'operator', contentReads: 0 }),
      ],
      totalCount: 2,
      page: 1,
      pageSize: 50,
    })
    mocks.getElevationStatus.mockResolvedValue({ maxDurationMinutes: 240, active: [] })
  })

  it('lists the elevations of all platform administrators with their reads', async () => {
    const wrapper = await mountView()
    await flushPromises()

    const items = wrapper.findAll('[data-testid="elevation-item"]')
    expect(items).toHaveLength(2)
    expect(items[0].text()).toContain('every client of Acme')
    expect(items[0].text()).toContain('SUP-1042')
    expect(items[0].text()).toContain('3 content reads')
    expect(items[0].text()).toContain('operator')
    expect(items[1].text()).toContain('second-operator')
    expect(items[1].text()).toContain('ended by operator')
    expect(items[1].find('[data-testid="elevation-end-item"]').exists()).toBe(false)
  })

  it('shows the records of an elevation', async () => {
    mocks.listElevationAuditEntries.mockResolvedValue([
      { id: 'a', summary: 'Platform administrator operator started an elevation.', detail: 'Reason: SUP-1042', occurredAt: '2026-10-26T09:00:00Z' },
      { id: 'b', summary: 'Platform administrator operator read the result of a review job of client Payments.', detail: null, occurredAt: '2026-10-26T09:05:00Z' },
    ])
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="elevation-records"]')[0].trigger('click')
    await flushPromises()

    expect(mocks.listElevationAuditEntries).toHaveBeenCalledWith('elevation-1')
    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).toContain('read the result of a review job')
  })

  it('keeps the records of each elevation when their requests finish in the other order', async () => {
    let answerFirst!: (value: unknown) => void
    mocks.listElevationAuditEntries
      .mockReturnValueOnce(new Promise((resolve) => {
        answerFirst = resolve
      }))
      .mockResolvedValueOnce([
        { id: 'b', summary: 'Record of the second elevation.', detail: null, occurredAt: '2026-10-26T09:05:00Z' },
      ])
    const wrapper = await mountView()
    await flushPromises()
    const buttons = wrapper.findAll('[data-testid="elevation-records"]')

    await buttons[0].trigger('click')
    await buttons[1].trigger('click')
    await flushPromises()
    answerFirst([{ id: 'a', summary: 'Record of the first elevation.', detail: null, occurredAt: '2026-10-26T09:00:00Z' }])
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).toContain('Record of the second elevation.')
    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).not.toContain('Record of the first elevation.')

    // The view loads the records again when an elevation is opened again. That request stays open here, so the list
    // shows the records the late answer stored for the first elevation.
    mocks.listElevationAuditEntries.mockReturnValueOnce(new Promise(() => {}))
    await buttons[0].trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).toContain('Record of the first elevation.')
    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).not.toContain('Record of the second elevation.')
  })

  it('keeps the newer records of an elevation when an older request for them finishes later', async () => {
    let answerOlder!: (value: unknown) => void
    mocks.listElevationAuditEntries
      .mockReturnValueOnce(new Promise((resolve) => {
        answerOlder = resolve
      }))
      .mockResolvedValueOnce([
        { id: 'b', summary: 'Newer record of the first elevation.', detail: null, occurredAt: '2026-10-26T09:05:00Z' },
      ])
    const wrapper = await mountView()
    await flushPromises()
    const button = wrapper.findAll('[data-testid="elevation-records"]')[0]

    await button.trigger('click')
    await button.trigger('click')
    await button.trigger('click')
    await flushPromises()
    answerOlder([{ id: 'a', summary: 'Older record of the first elevation.', detail: null, occurredAt: '2026-10-26T09:00:00Z' }])
    await flushPromises()

    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).toContain('Newer record of the first elevation.')
    expect(wrapper.get('[data-testid="elevation-record-list"]').text()).not.toContain('Older record')
  })

  it('keeps the list and its controls when the records of an elevation cannot be loaded', async () => {
    mocks.listElevationAuditEntries.mockRejectedValue(new Error('The records of the elevation could not be loaded.'))
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.findAll('[data-testid="elevation-records"]')[0].trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="elevations-error"]').text()).toContain('could not be loaded')
    expect(wrapper.findAll('[data-testid="elevation-item"]')).toHaveLength(2)
    expect(wrapper.find('[data-testid="elevation-end-item"]').exists()).toBe(true)
  })

  it('keeps the loaded elevations when an older page cannot be loaded', async () => {
    mocks.listElevations
      .mockResolvedValueOnce({ items: [elevation()], totalCount: 2, page: 1, pageSize: 50 })
      .mockRejectedValueOnce(new Error('The elevations could not be loaded.'))
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="elevations-older"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="elevations-error"]').text()).toContain('The elevations could not be loaded.')
    expect(wrapper.findAll('[data-testid="elevation-item"]')).toHaveLength(1)
    expect(wrapper.find('[data-testid="elevations-older"]').exists()).toBe(true)
  })

  it('drops an older page that arrives after the reload that follows an end', async () => {
    let answerOlderPage!: (value: unknown) => void
    mocks.listElevations
      .mockResolvedValueOnce({
        items: [elevation(), elevation({ id: 'elevation-2', administratorName: 'second-operator' })],
        totalCount: 3,
        page: 1,
        pageSize: 50,
      })
      .mockReturnValueOnce(new Promise((resolve) => {
        answerOlderPage = resolve
      }))
      .mockResolvedValueOnce({
        items: [elevation({ state: 'ended', endedByName: 'operator' }), elevation({ id: 'elevation-2', administratorName: 'second-operator' })],
        totalCount: 3,
        page: 1,
        pageSize: 50,
      })
    mocks.endElevation.mockResolvedValue(elevation({ state: 'ended' }))
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="elevations-older"]').trigger('click')
    await wrapper.findAll('[data-testid="elevation-end-item"]')[0].trigger('click')
    await flushPromises()
    answerOlderPage({
      items: [elevation({ id: 'elevation-3', administratorName: 'third-operator', state: 'expired' })],
      totalCount: 3,
      page: 2,
      pageSize: 50,
    })
    await flushPromises()

    const items = wrapper.findAll('[data-testid="elevation-item"]')
    expect(items).toHaveLength(2)
    expect(items[0].text()).toContain('ended by operator')
    expect(wrapper.text()).not.toContain('third-operator')
    mocks.listElevations.mockResolvedValueOnce({ items: [], totalCount: 3, page: 2, pageSize: 50 })
    await wrapper.get('[data-testid="elevations-older"]').trigger('click')
    expect(mocks.listElevations).toHaveBeenLastCalledWith(2, 50)
  })

  it('keeps the end of an elevation disabled while its request runs, also when another end finishes first', async () => {
    let finishFirstEnd!: (value: unknown) => void
    mocks.listElevations.mockResolvedValue({
      items: [elevation(), elevation({ id: 'elevation-2', administratorName: 'second-operator' })],
      totalCount: 2,
      page: 1,
      pageSize: 50,
    })
    mocks.endElevation
      .mockReturnValueOnce(new Promise((resolve) => {
        finishFirstEnd = resolve
      }))
      .mockReturnValueOnce(new Promise(() => {}))
    const wrapper = await mountView()
    await flushPromises()
    const buttons = wrapper.findAll('[data-testid="elevation-end-item"]')

    await buttons[0].trigger('click')
    await buttons[1].trigger('click')
    finishFirstEnd(elevation({ state: 'ended' }))
    await flushPromises()

    const second = wrapper.findAll('[data-testid="elevation-end-item"]')[1]
    expect(second.attributes('disabled')).toBeDefined()
    expect(second.text()).toBe('Ending…')
    expect(mocks.endElevation).toHaveBeenCalledTimes(2)
  })

  it('ends an active elevation and reloads the list', async () => {
    mocks.endElevation.mockResolvedValue(elevation({ state: 'ended' }))
    const wrapper = await mountView()
    await flushPromises()

    await wrapper.get('[data-testid="elevation-end-item"]').trigger('click')
    await flushPromises()

    expect(mocks.endElevation).toHaveBeenCalledWith('elevation-1')
    expect(mocks.listElevations).toHaveBeenCalledTimes(2)
  })
})
