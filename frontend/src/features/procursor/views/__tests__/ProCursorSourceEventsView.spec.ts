// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ElevationRequiredError } from '@/services/elevationRequirement'

const mocks = vi.hoisted(() => ({
  getProCursorRecentEvents: vi.fn(),
}))

vi.mock('@/services/proCursorService', () => mocks)
vi.mock('vue-router', () => ({
  useRoute: () => ({ params: { id: 'client-1', sourceId: 'source-1' } }),
}))

function page(sourcePath: string) {
  return { items: [{ requestId: `request-${sourcePath}`, sourcePath, occurredAtUtc: new Date().toISOString() }] }
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => {
    resolve = done
  })
  return { promise, resolve }
}

async function mountView() {
  const { default: ProCursorSourceEventsView } = await import('../ProCursorSourceEventsView.vue')
  return mount(ProCursorSourceEventsView, {
    global: {
      stubs: {
        'router-link': { template: '<a><slot /></a>' },
        ElevationRequiredNotice: {
          emits: ['started'],
          template: '<section data-testid="elevation-required"><button data-testid="started" @click="$emit(\'started\')" /></section>',
        },
        ProCursorUsageRecentEventsTable: {
          props: ['items', 'loading', 'error'],
          template: '<ul data-testid="events"><li v-for="item in items" :key="item.requestId">{{ item.sourcePath }}</li></ul>',
        },
      },
    },
  })
}

describe('ProCursorSourceEventsView for a platform administrator', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('drops the events once an elevation is required and keeps the notice until a request succeeds', async () => {
    const retry = deferred<ReturnType<typeof page>>()
    mocks.getProCursorRecentEvents
      .mockResolvedValueOnce(page('src/billing/ledger.ts'))
      .mockRejectedValueOnce(new ElevationRequiredError({ tenantId: 'tenant-1', clientId: 'client-1' }))
      .mockReturnValueOnce(retry.promise)
    const wrapper = await mountView()
    await flushPromises()
    expect(wrapper.get('[data-testid="events"]').text()).toContain('src/billing/ledger.ts')

    await wrapper.get('button[title="Refresh"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="elevation-required"]').exists()).toBe(true)

    await wrapper.get('[data-testid="started"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="elevation-required"]').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('src/billing/ledger.ts')

    retry.resolve(page('src/billing/invoice.ts'))
    await flushPromises()
    expect(wrapper.find('[data-testid="elevation-required"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="events"]').text()).toContain('src/billing/invoice.ts')
    expect(wrapper.text()).not.toContain('src/billing/ledger.ts')
    expect(mocks.getProCursorRecentEvents).toHaveBeenCalledTimes(3)
    for (const call of mocks.getProCursorRecentEvents.mock.calls) {
      expect(call).toEqual(['client-1', 'source-1', 250])
    }
  })
})
