// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const getMock = vi.fn()
let capabilityState: Array<{ key?: string | null; isAvailable?: boolean; message?: string | null }> = []

vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: getMock, DELETE: vi.fn() })),
  getApiErrorMessage: (error: unknown, fallback: string) => (error instanceof Error ? error.message : fallback),
}))

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({
    notify: vi.fn(),
  }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    getCapability: (key: string) => capabilityState.find((capability) => capability.key === key) ?? null,
  }),
}))

async function mountTab() {
  const { default: ClientCrawlConfigsTab } = await import('@/components/ClientCrawlConfigsTab.vue')
  return mount(ClientCrawlConfigsTab, {
    props: { clientId: 'client-1' },
    global: {
      stubs: {
        ProgressOrb: { template: '<div class="orb-stub" />' },
        ModalDialog: { template: '<div><slot /><slot name="footer" /></div>' },
        ConfirmDialog: { template: '<div />' },
        CrawlConfigForm: { template: '<div class="crawl-config-form-stub" />' },
      },
    },
  })
}

function setCapabilities(capabilities: Array<{ key: string; isAvailable: boolean; message?: string }>) {
  capabilityState = capabilities.map((capability) => ({
    key: capability.key,
    displayName: capability.key,
    requiresCommercial: true,
    defaultWhenCommercial: true,
    overrideState: 'default',
    isAvailable: capability.isAvailable,
    message: capability.message ?? null,
  }))
}

describe('ClientCrawlConfigsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    capabilityState = []
    setCapabilities([{ key: 'crawl-configs', isAvailable: true }])
    getMock.mockResolvedValue({ data: [], response: { ok: true } })
  })

  it('shows an unavailable state and skips loading when crawl configs are disabled', async () => {
    setCapabilities([{ key: 'crawl-configs', isAvailable: false, message: 'Crawl configs require commercial.' }])

    const wrapper = await mountTab()
    await flushPromises()

    expect(getMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Crawl Configs are unavailable')
    expect(wrapper.text()).toContain('Crawl configs require commercial.')
    expect(wrapper.text()).not.toContain('New Config')
    expect(wrapper.text()).not.toContain('Create Config')
  })
})
