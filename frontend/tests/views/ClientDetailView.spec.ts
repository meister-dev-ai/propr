// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockGet = vi.fn()
const mockPatch = vi.fn()
const mockPut = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet, PATCH: mockPatch, PUT: mockPut })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

const mockRouterPush = vi.fn()
const mockRouterReplace = vi.fn()
const mockRoute = {
  params: { id: 'client-1' },
  query: {} as Record<string, string>,
}
const hasClientRoleMock = vi.fn((_clientId: string, minRole: 0 | 1) => minRole <= 1)
let capabilityState: Array<{ key?: string | null; isAvailable?: boolean; message?: string | null }> = []

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush, replace: mockRouterReplace }),
  useRoute: () => mockRoute,
  RouterLink: { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' },
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: hasClientRoleMock,
    getCapability: (key: string) => capabilityState.find((capability) => capability.key === key) ?? null,
    setLicensingState: (_edition: string, capabilities: Array<{ key?: string | null; isAvailable?: boolean; message?: string | null }>) => {
      capabilityState = capabilities
    },
    clearTokens: () => {
      capabilityState = []
    },
  }),
}))

vi.mock('@/components/dialogs/ConfirmDialog.vue', () => ({
  default: {
    name: 'ConfirmDialog',
    props: ['open', 'message'],
    emits: ['confirm', 'cancel'],
    template: '<div class="confirm-dialog-stub" />',
  },
}))

vi.mock('@/features/clients/components/ClientCrawlConfigsTab.vue', () => ({
  default: {
    name: 'ClientCrawlConfigsTab',
    props: ['clientId'],
    template: '<div class="client-crawl-configs-tab-stub" :data-client-id="clientId">crawl tab</div>',
  },
}))

vi.mock('@/features/clients/components/ClientWebhookConfigsTab.vue', () => ({
  default: {
    name: 'ClientWebhookConfigsTab',
    props: ['clientId'],
    template: '<div class="client-webhook-configs-tab-stub" :data-client-id="clientId">webhook tab</div>',
  },
}))

vi.mock('@/features/clients/components/ClientProviderConnectionsTab.vue', () => ({
  default: {
    name: 'ClientProviderConnectionsTab',
    props: ['clientId'],
    template: '<div class="client-provider-connections-tab-stub" :data-client-id="clientId">provider tab</div>',
  },
}))

vi.mock('@/features/clients/components/ClientOverview.vue', () => ({
  default: {
    name: 'ClientOverview',
    props: ['clientId'],
    template: '<div class="client-overview-stub" :data-client-id="clientId">overview cards</div>',
  },
}))

vi.mock('@/features/clients/components/ClientProCursorTab.vue', () => ({
  default: {
    name: 'ClientProCursorTab',
    props: ['clientId'],
    template: '<div class="client-procursor-tab-stub" :data-client-id="clientId">procursor tab</div>',
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

vi.mock('@/features/reviews/components/ReviewHistorySection.vue', () => ({
  default: {
    name: 'ReviewHistorySection',
    props: ['clientId'],
    template: '<div class="review-history-section-stub" :data-client-id="clientId">review history</div>',
  },
}))

vi.mock('@/components/text/TextViewerModal.vue', () => ({
  default: {
    name: 'TextViewerModal',
    props: ['isOpen', 'title', 'text', 'plainText'],
    template: '<div class="text-viewer-modal-stub" />',
  },
}))

vi.mock('@/features/clients/components/ClientAiConnectionsTab.vue', () => ({
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
  defaultReviewStrategy: 'fileByFile',
  defaultReviewPipelineProfileId: 'file-by-file-balanced',
  defaultReviewPipelineProfileUpdatedAtUtc: null,
  scmCommentPostingEnabled: true,
  enableEvidenceBackedVerification: false,
  enableMultiPassUnion: false,
}

const sampleReviewProfiles = {
  profiles: [
    { profileId: 'file-by-file-calm', displayName: 'Calm', isDefault: false },
    { profileId: 'file-by-file-balanced', displayName: 'Balanced', isDefault: true },
    { profileId: 'file-by-file-assertive', displayName: 'Assertive', isDefault: false },
  ],
}

const sampleClientReviewProfile = {
  clientId: 'client-1',
  defaultReviewPipelineProfileId: 'file-by-file-balanced',
  source: 'systemDefault',
  updatedAtUtc: null,
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

describe('ClientDetailView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'true')
    mockRoute.query = {}
    mockRouterReplace.mockReset()
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: 0 | 1) => minRole <= 1)
    capabilityState = []
    setCapabilities([
      { key: 'crawl-configs', isAvailable: true },
      { key: 'multiple-scm-providers', isAvailable: true },
    ])
    mockGet.mockReset()
    mockGet
      .mockResolvedValueOnce({ data: sampleClient })
      .mockResolvedValueOnce({ data: sampleReviewProfiles })
      .mockResolvedValueOnce({ data: sampleClientReviewProfile })
    mockPut.mockResolvedValue({ data: sampleClientReviewProfile, response: { ok: true } })
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
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    expect(wrapper.find('.page-with-sidebar-layout').exists()).toBe(true)
    const input = wrapper.find('input[name="displayName"]')
    expect(input.exists()).toBe(true)
    expect((input.element as HTMLInputElement).value).toBe('Acme Corp')
  }, 10000)

  it('calls PATCH with updated displayName on Save', async () => {
    mockPatch.mockResolvedValue({ data: { ...sampleClient, displayName: 'New Name' }, response: { ok: true } })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
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
    mockPatch.mockResolvedValue({ data: { ...sampleClient, isActive: false }, response: { ok: true } })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
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

  it('loads the SCM comment posting setting and saves the disabled value', async () => {
    mockPatch.mockResolvedValue({ data: { ...sampleClient, scmCommentPostingEnabled: false }, response: { ok: true } })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const checkbox = wrapper.find('input[name="scmCommentPostingEnabled"]')
    expect(checkbox.exists()).toBe(true)
    expect((checkbox.element as HTMLInputElement).checked).toBe(true)

    await checkbox.setValue(false)
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/clients/{clientId}',
        expect.objectContaining({
          params: { path: { clientId: 'client-1' } },
          body: { defaultReviewStrategy: 'fileByFile', scmCommentPostingEnabled: false, enableEvidenceBackedVerification: false, enableMultiPassUnion: false, enableLanguageRobustScreening: false },
        })
    )
  })

  it('saves the SCM comment posting setting when re-enabled', async () => {
    mockGet.mockReset()
    mockGet
      .mockResolvedValueOnce({ data: { ...sampleClient, scmCommentPostingEnabled: false } })
      .mockResolvedValueOnce({ data: sampleReviewProfiles })
      .mockResolvedValueOnce({ data: sampleClientReviewProfile })
    mockPatch.mockResolvedValue({ data: sampleClient, response: { ok: true } })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const checkbox = wrapper.find('input[name="scmCommentPostingEnabled"]')
    expect((checkbox.element as HTMLInputElement).checked).toBe(false)

    await checkbox.setValue(true)
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/clients/{clientId}',
        expect.objectContaining({
          params: { path: { clientId: 'client-1' } },
          body: { defaultReviewStrategy: 'fileByFile', scmCommentPostingEnabled: true, enableEvidenceBackedVerification: false, enableMultiPassUnion: false, enableLanguageRobustScreening: false },
        })
    )
  })

  it('loads and saves the default review strategy from advanced settings', async () => {
    mockPatch.mockResolvedValue({ data: { ...sampleClient, defaultReviewStrategy: 'prWideAgentic' }, response: { ok: true } })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const strategySelect = wrapper.find('select[name="defaultReviewStrategy"]')
    expect(strategySelect.exists()).toBe(true)
    expect((strategySelect.element as HTMLSelectElement).value).toBe('fileByFile')

    await strategySelect.setValue('prWideAgentic')
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/clients/{clientId}',
        expect.objectContaining({
          params: { path: { clientId: 'client-1' } },
          body: { defaultReviewStrategy: 'prWideAgentic', scmCommentPostingEnabled: true, enableEvidenceBackedVerification: false, enableMultiPassUnion: false, enableLanguageRobustScreening: false },
        })
    )
  })

  it('loads and saves the agentic file-by-file default review strategy from advanced settings', async () => {
    mockPatch.mockResolvedValue({ data: { ...sampleClient, defaultReviewStrategy: 'agenticFileByFile' }, response: { ok: true } })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const strategySelect = wrapper.find('select[name="defaultReviewStrategy"]')
    expect(strategySelect.exists()).toBe(true)

    await strategySelect.setValue('agenticFileByFile')
    await wrapper.find('button.scm-advanced-settings-save-btn').trigger('click')
    await flushPromises()

    expect(mockPatch).toHaveBeenCalledWith(
      '/clients/{clientId}',
        expect.objectContaining({
          params: { path: { clientId: 'client-1' } },
          body: { defaultReviewStrategy: 'agenticFileByFile', scmCommentPostingEnabled: true, enableEvidenceBackedVerification: false, enableMultiPassUnion: false, enableLanguageRobustScreening: false },
        })
    )
  })

  it('shows not-found message and navigates home on 404', async () => {
    mockGet.mockReset()
    mockGet.mockResolvedValueOnce({ data: null, response: { status: 404, ok: false } })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()
    expect(wrapper.text()).toContain('Client not found')
    expect(mockRouterPush).toHaveBeenCalledWith({ name: 'clients' })
  })

  it('loads and saves the review aggressiveness profile from advanced settings', async () => {
    mockPut.mockResolvedValue({
      data: {
        clientId: 'client-1',
        defaultReviewPipelineProfileId: 'file-by-file-assertive',
        source: 'clientDefault',
        updatedAtUtc: '2026-05-31T12:00:00Z',
      },
      response: { ok: true },
    })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const profileSelect = wrapper.find('select[name="defaultReviewPipelineProfileId"]')
    expect(profileSelect.exists()).toBe(true)
    expect((profileSelect.element as HTMLSelectElement).value).toBe('file-by-file-balanced')

    await profileSelect.setValue('file-by-file-assertive')
    await wrapper.find('button.review-profile-save-btn').trigger('click')
    await flushPromises()

    expect(mockPut).toHaveBeenCalledWith(
      '/admin/clients/{clientId}/review-profile',
      expect.objectContaining({
        params: { path: { clientId: 'client-1' } },
        body: { defaultReviewPipelineProfileId: 'file-by-file-assertive' },
      })
    )
  })

  it('shows the client overview cards in the system tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-overview-stub').attributes('data-client-id')).toBe('client-1')
    expect(wrapper.text()).toContain('overview cards')
    expect(wrapper.text()).toContain('Danger Zone')
  })

  it('passes the current client ID into the crawl configuration tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-crawl-configs-tab-stub').attributes('data-client-id')).toBe('client-1')
  })

  it('passes the current client ID into the webhook configuration tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-webhook-configs-tab-stub').attributes('data-client-id')).toBe('client-1')
  })

  it('passes the current client ID into the provider connections tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
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
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
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
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const usageTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('Tokens & Usage'))
    expect(usageTab).toBeDefined()
    await usageTab!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.usage-dashboard-stub').attributes('data-client-id')).toBe('client-1')
    expect(wrapper.text()).toContain('procursor usage dashboard')
  })

  it('hides Crawl Configs navigation when that capability is unavailable', async () => {
    setCapabilities([
      { key: 'crawl-configs', isAvailable: false, message: 'Crawl configs require commercial.' },
      { key: 'multiple-scm-providers', isAvailable: true },
    ])
    mockGet.mockResolvedValue({ data: sampleClient })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const navTexts = wrapper.findAll('button.sidebar-nav-link').map((button) => button.text())
    expect(navTexts.some((text) => text.includes('Crawl Configs'))).toBe(false)
    expect(navTexts.some((text) => text.includes('ProCursor'))).toBe(true)
    expect(navTexts.some((text) => text.includes('Tokens & Usage'))).toBe(true)
  })

  it('keeps direct ProCursor tab navigation available without a ProCursor capability entry', async () => {
    mockRoute.query = { tab: 'procursor' }
    mockGet.mockResolvedValue({ data: sampleClient })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-procursor-tab-stub').exists()).toBe(true)
    expect(wrapper.text()).not.toContain('Add Source')
  })

  it('hides the usage analytics tab when the rollout flag is disabled', async () => {
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'false')
    mockGet.mockResolvedValue({ data: sampleClient })

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const usageTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('Tokens & Usage'))
    expect(usageTab).toBeUndefined()
  })

  it('renders the guided admin entrypoints within the quickstart timing budget', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })

    const startedAt = Date.now()
    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
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

    const { default: ClientDetailView } = await import('@/features/clients/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    await openAiConnectionsTab(wrapper)

    expect(wrapper.find('.client-ai-connections-tab-stub').attributes('data-client-id')).toBe('client-1')
  })
})
