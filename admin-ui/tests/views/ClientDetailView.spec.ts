// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockGet = vi.fn()
const mockPatch = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet, PATCH: mockPatch })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

const mockRouterPush = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
  useRoute: () => ({ params: { id: 'client-1' } }),
  RouterLink: { template: '<a><slot /></a>' },
}))

vi.mock('@/components/ConfirmDialog.vue', () => ({
  default: {
    name: 'ConfirmDialog',
    props: ['open', 'message'],
    emits: ['confirm', 'cancel'],
    template: '<div class="confirm-dialog-stub" />',
  },
}))

vi.mock('@/components/ClientCrawlConfigsTab.vue', () => ({
  default: {
    name: 'ClientCrawlConfigsTab',
    props: ['clientId'],
    template: '<div class="client-crawl-configs-tab-stub" :data-client-id="clientId">crawl tab</div>',
  },
}))

vi.mock('@/components/ClientWebhookConfigsTab.vue', () => ({
  default: {
    name: 'ClientWebhookConfigsTab',
    props: ['clientId'],
    template: '<div class="client-webhook-configs-tab-stub" :data-client-id="clientId">webhook tab</div>',
  },
}))

vi.mock('@/components/ClientProviderConnectionsTab.vue', () => ({
  default: {
    name: 'ClientProviderConnectionsTab',
    props: ['clientId'],
    template: '<div class="client-provider-connections-tab-stub" :data-client-id="clientId">provider tab</div>',
  },
}))

vi.mock('@/components/ClientOverview.vue', () => ({
  default: {
    name: 'ClientOverview',
    props: ['clientId'],
    template: '<div class="client-overview-stub" :data-client-id="clientId">overview cards</div>',
  },
}))

vi.mock('@/components/ProviderConnectionStatusList.vue', () => ({
  default: {
    name: 'ProviderConnectionStatusList',
    props: ['clientId'],
    template: '<div class="provider-connection-status-list-stub" :data-client-id="clientId">provider status</div>',
  },
}))

vi.mock('@/components/ProviderConnectionAuditTrail.vue', () => ({
  default: {
    name: 'ProviderConnectionAuditTrail',
    props: ['clientId'],
    template: '<div class="provider-connection-audit-trail-stub" :data-client-id="clientId">provider audit</div>',
  },
}))

vi.mock('@/components/UsageDashboard.vue', () => ({
  default: {
    name: 'UsageDashboard',
    props: ['clientId'],
    template: '<div class="usage-dashboard-stub" :data-client-id="clientId">procursor usage dashboard</div>',
  },
}))

vi.mock('@/components/ClientAiConnectionsTab.vue', () => ({
  default: {
    name: 'ClientAiConnectionsTab',
    props: ['clientId'],
    template: '<div class="client-ai-connections-tab-stub" :data-client-id="clientId">ai tab</div>',
  },
}))

const sampleClient = {
  id: 'client-1',
  displayName: 'Acme Corp',
  isActive: true,
  createdAt: '2024-01-01T00:00:00Z',
}

describe('ClientDetailView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'true')
  })

  afterEach(() => {
    vi.unstubAllEnvs()
  })

  async function openAiConnectionsTab(wrapper: ReturnType<typeof mount>) {
    const aiTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('AI Providers'))
    expect(aiTab).toBeDefined()
    await aiTab!.trigger('click')
    await flushPromises()
  }

  it('fetches client on mount and renders displayName in an editable input', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    const input = wrapper.find('input[name="displayName"]')
    expect(input.exists()).toBe(true)
    expect((input.element as HTMLInputElement).value).toBe('Acme Corp')
  })

  it('calls PATCH with updated displayName on Save', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    mockPatch.mockResolvedValue({ data: { ...sampleClient, displayName: 'New Name' }, response: { ok: true } })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    await wrapper.find('input[name="displayName"]').setValue('New Name')
    await wrapper.find('button.save-btn').trigger('click')
    await flushPromises()
    expect(mockPatch).toHaveBeenCalledWith(
      '/clients/{clientId}',
      expect.objectContaining({ params: { path: { clientId: 'client-1' } }, body: { displayName: 'New Name' } })
    )
  })

  it('calls PATCH with toggled isActive on Disable button', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    mockPatch.mockResolvedValue({ data: { ...sampleClient, isActive: false }, response: { ok: true } })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    const toggleBtn = wrapper.find('button.toggle-status-btn')
    expect(toggleBtn.text()).toBe('Disable')
    await toggleBtn.trigger('click')
    await flushPromises()
    expect(mockPatch).toHaveBeenCalledWith(
      '/clients/{clientId}',
      expect.objectContaining({ params: { path: { clientId: 'client-1' } }, body: { isActive: false } })
    )
  })

  it('shows not-found message and navigates home on 404', async () => {
    mockGet.mockResolvedValue({ data: null, response: { status: 404, ok: false } })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    expect(wrapper.text()).toContain('Client not found')
    expect(mockRouterPush).toHaveBeenCalledWith({ name: 'clients' })
  })

  it('shows the client overview cards in the system tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-overview-stub').attributes('data-client-id')).toBe('client-1')
    expect(wrapper.text()).toContain('overview cards')
    expect(wrapper.text()).toContain('Danger Zone')
  })

  it('passes the current client ID into the crawl configuration tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-crawl-configs-tab-stub').attributes('data-client-id')).toBe('client-1')
  })

  it('passes the current client ID into the webhook configuration tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-webhook-configs-tab-stub').attributes('data-client-id')).toBe('client-1')
  })

  it('passes the current client ID into the provider connections tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const providersTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('SCM Providers'))
    expect(providersTab).toBeDefined()
    await providersTab!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.client-provider-connections-tab-stub').attributes('data-client-id')).toBe('client-1')
  })

  it('keeps the providers tab available from the detail page', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const providersTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('SCM Providers'))
    expect(providersTab).toBeDefined()

    await providersTab!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.client-provider-connections-tab-stub').exists()).toBe(true)
  })

  it('passes the current client ID into the usage dashboard tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const usageTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('Tokens & Usage'))
    expect(usageTab).toBeDefined()
    await usageTab!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.usage-dashboard-stub').attributes('data-client-id')).toBe('client-1')
    expect(wrapper.text()).toContain('procursor usage dashboard')
  })

  it('hides the usage analytics tab when the rollout flag is disabled', async () => {
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'false')
    mockGet.mockResolvedValue({ data: sampleClient })

    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const usageTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('Tokens & Usage'))
    expect(usageTab).toBeUndefined()
  })

  it('renders the guided admin entrypoints within the quickstart timing budget', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })

    const startedAt = Date.now()
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    const elapsedMs = Date.now() - startedAt

    expect(wrapper.find('.client-overview-stub').exists()).toBe(true)
    expect(wrapper.find('.client-crawl-configs-tab-stub').exists()).toBe(true)
    expect(wrapper.text()).toContain('SCM Providers')
    expect(elapsedMs).toBeLessThan(2000)
  })

  it('passes the current client ID into the AI profiles tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })

    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    await openAiConnectionsTab(wrapper)

    expect(wrapper.find('.client-ai-connections-tab-stub').attributes('data-client-id')).toBe('client-1')
  })
})
