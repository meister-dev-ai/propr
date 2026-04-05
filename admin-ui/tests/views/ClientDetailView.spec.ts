// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockGet = vi.fn()
const mockPatch = vi.fn()
const mockListAiConnections = vi.fn()
const mockCreateAiConnection = vi.fn()
const mockUpdateAiConnection = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet, PATCH: mockPatch })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('@/services/aiConnectionsService', () => ({
  listAiConnections: mockListAiConnections,
  createAiConnection: mockCreateAiConnection,
  updateAiConnection: mockUpdateAiConnection,
  activateAiConnection: vi.fn(),
  deactivateAiConnection: vi.fn(),
  deleteAiConnection: vi.fn(),
}))

const mockRouterPush = vi.fn()
vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush }),
  useRoute: () => ({ params: { id: 'client-1' } }),
  RouterLink: { template: '<a><slot /></a>' },
}))

vi.mock('@/components/AdoCredentialsForm.vue', () => ({
  default: {
    name: 'AdoCredentialsForm',
    props: ['clientId', 'hasCredentials'],
    emits: ['credentials-updated', 'credentials-cleared'],
    template: `
      <div class="ado-form-stub" :data-client-id="clientId" :data-has-credentials="String(hasCredentials)">
        <button class="emit-credentials-updated" @click="$emit('credentials-updated')">emit updated</button>
        <button class="emit-credentials-cleared" @click="$emit('credentials-cleared')">emit cleared</button>
      </div>
    `,
  },
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

vi.mock('@/components/UsageDashboard.vue', () => ({
  default: {
    name: 'UsageDashboard',
    props: ['clientId'],
    template: '<div class="usage-dashboard-stub" :data-client-id="clientId">procursor usage dashboard</div>',
  },
}))

const sampleClient = {
  id: 'client-1',
  displayName: 'Acme Corp',
  isActive: true,
  hasAdoCredentials: false,
  createdAt: '2024-01-01T00:00:00Z',
}

describe('ClientDetailView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    vi.stubEnv('VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING', 'true')
    mockListAiConnections.mockResolvedValue([])
  })

  afterEach(() => {
    vi.unstubAllEnvs()
  })

  async function openAiConnectionsTab(wrapper: ReturnType<typeof mount>) {
    const aiTab = wrapper.findAll('button.sidebar-nav-link').find((button) => button.text().includes('AI Connections'))
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

  it('passes the client state to AdoCredentialsForm and reacts to credential events', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    const adoForm = wrapper.find('.ado-form-stub')
    expect(adoForm.attributes('data-client-id')).toBe('client-1')
    expect(adoForm.attributes('data-has-credentials')).toBe('false')
    expect(wrapper.text()).toContain('Not configured')
    expect(wrapper.text()).toContain('Requires ADO credentials')

    await wrapper.find('.emit-credentials-updated').trigger('click')
    await flushPromises()

    expect(wrapper.find('.ado-form-stub').attributes('data-has-credentials')).toBe('true')
    expect(wrapper.text()).toContain('Configured')
    expect(wrapper.find('input[name="reviewerOrgUrl"]').exists()).toBe(true)

    await wrapper.find('.emit-credentials-cleared').trigger('click')
    await flushPromises()

    expect(wrapper.find('.ado-form-stub').attributes('data-has-credentials')).toBe('false')
    expect(wrapper.text()).toContain('Not configured')
    expect(wrapper.text()).toContain('Requires ADO credentials')
  })

  it('passes the current client ID into the crawl configuration tab', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    expect(wrapper.find('.client-crawl-configs-tab-stub').attributes('data-client-id')).toBe('client-1')
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

    expect(wrapper.find('.ado-form-stub').exists()).toBe(true)
    expect(wrapper.find('.client-crawl-configs-tab-stub').exists()).toBe(true)
    expect(elapsedMs).toBeLessThan(2000)
  })

  it('opens the AI edit modal and saves endpoint and models for an existing connection', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    mockListAiConnections.mockResolvedValue([
      {
        id: 'conn-1',
        displayName: 'Azure OpenAI Prod',
        endpointUrl: 'https://old-resource.openai.azure.com/',
        models: ['gpt-4o'],
        isActive: true,
        activeModel: 'gpt-4o',
        modelCategory: null,
      },
    ])
    mockUpdateAiConnection.mockResolvedValue({
      id: 'conn-1',
      displayName: 'Azure OpenAI Staging',
      endpointUrl: 'https://new-resource.openai.azure.com/',
      models: ['gpt-4.1', 'gpt-4.1-mini'],
      isActive: true,
      activeModel: 'gpt-4.1',
      modelCategory: null,
    })

    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

  await openAiConnectionsTab(wrapper)

    await wrapper.find('.ai-conn-main').trigger('click')
    await flushPromises()

    await wrapper.find('input[name="editAiDisplayName"]').setValue('Azure OpenAI Staging')
    await wrapper.find('input[name="editAiEndpointUrl"]').setValue('https://new-resource.openai.azure.com/')
    await wrapper.find('input[name="editAiModels"]').setValue('gpt-4.1, gpt-4.1-mini')
    await wrapper.find('input[name="editAiApiKey"]').setValue('replacement-secret')
    await wrapper.find('button.save-ai-edit-btn').trigger('click')
    await flushPromises()

    expect(mockUpdateAiConnection).toHaveBeenCalledWith('client-1', 'conn-1', {
      displayName: 'Azure OpenAI Staging',
      endpointUrl: 'https://new-resource.openai.azure.com/',
      models: ['gpt-4.1', 'gpt-4.1-mini'],
      apiKey: 'replacement-secret',
    })
  })

  it('creates an embedding connection with per-model capability metadata', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    mockCreateAiConnection.mockResolvedValue({
      id: 'conn-embed',
      displayName: 'Embedding Pool',
      endpointUrl: 'https://embeddings.openai.azure.com/',
      models: ['text-embedding-3-large'],
      isActive: false,
      activeModel: null,
      modelCategory: 'embedding',
      modelCapabilities: [
        {
          modelName: 'text-embedding-3-large',
          tokenizerName: 'cl100k_base',
          maxInputTokens: 8192,
          embeddingDimensions: 3072,
        },
      ],
    })

    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    await openAiConnectionsTab(wrapper)

    const addConnectionButton = wrapper.findAll('button').find((button) => button.text().includes('Add Connection'))
    expect(addConnectionButton).toBeDefined()
    await addConnectionButton!.trigger('click')
    await flushPromises()

    await wrapper.find('.ai-create-form input[placeholder="e.g. Azure OpenAI (prod)"]').setValue('Embedding Pool')
    await wrapper.find('.ai-create-form input[placeholder="https://my-resource.openai.azure.com/"]').setValue('https://embeddings.openai.azure.com/')
    await wrapper.find('.ai-create-form input[placeholder="Paste your API key"]').setValue('embedding-secret')
    await wrapper.find('.ai-create-form input[placeholder="e.g. gpt-4o, gpt-4o-mini"]').setValue('text-embedding-3-large')
    await wrapper.findAll('.ai-create-form select')[0].setValue('embedding')
    await flushPromises()

    const capabilityRow = wrapper.find('.ai-create-form .ai-capability-row')
    expect(capabilityRow.exists()).toBe(true)
    await capabilityRow.find('select.capability-select').setValue('cl100k_base')
    const numericInputs = capabilityRow.findAll('input[type="number"]')
    await numericInputs[0].setValue('8192')
    await numericInputs[1].setValue('3072')

    const saveConnectionButton = wrapper.findAll('button').find((button) => button.text().includes('Save Connection'))
    expect(saveConnectionButton).toBeDefined()
    await saveConnectionButton!.trigger('click')
    await flushPromises()

    expect(mockCreateAiConnection).toHaveBeenCalledWith('client-1', {
      displayName: 'Embedding Pool',
      endpointUrl: 'https://embeddings.openai.azure.com/',
      models: ['text-embedding-3-large'],
      apiKey: 'embedding-secret',
      modelCategory: 'embedding',
      modelCapabilities: [
        {
          modelName: 'text-embedding-3-large',
          tokenizerName: 'cl100k_base',
          maxInputTokens: 8192,
          embeddingDimensions: 3072,
        },
      ],
    })
  })

  it('saves embedding capability edits for an existing embedding connection', async () => {
    mockGet.mockResolvedValue({ data: sampleClient })
    mockListAiConnections.mockResolvedValue([
      {
        id: 'conn-embed',
        displayName: 'Embedding Pool',
        endpointUrl: 'https://embeddings.openai.azure.com/',
        models: ['text-embedding-3-large'],
        isActive: false,
        activeModel: null,
        modelCategory: 'embedding',
        modelCapabilities: [
          {
            modelName: 'text-embedding-3-large',
            tokenizerName: 'cl100k_base',
            maxInputTokens: 8192,
            embeddingDimensions: 3072,
          },
        ],
      },
    ])
    mockUpdateAiConnection.mockResolvedValue({
      id: 'conn-embed',
      displayName: 'Embedding Pool',
      endpointUrl: 'https://embeddings.openai.azure.com/',
      models: ['text-embedding-3-large'],
      isActive: false,
      activeModel: null,
      modelCategory: 'embedding',
      modelCapabilities: [
        {
          modelName: 'text-embedding-3-large',
          tokenizerName: 'o200k_base',
          maxInputTokens: 16384,
          embeddingDimensions: 3072,
        },
      ],
    })

    const { default: ClientDetailView } = await import('@/views/ClientDetailView.vue')
    const wrapper = mount(ClientDetailView)
    await flushPromises()

    await openAiConnectionsTab(wrapper)

    await wrapper.find('.ai-conn-main').trigger('click')
    await flushPromises()

    const capabilityRow = wrapper.find('.ai-edit-form-grid .ai-capability-row')
    expect(capabilityRow.exists()).toBe(true)
    await capabilityRow.find('select.capability-select').setValue('o200k_base')
    const numericInputs = capabilityRow.findAll('input[type="number"]')
    await numericInputs[0].setValue('16384')
    await numericInputs[1].setValue('3072')

    await wrapper.find('button.save-ai-edit-btn').trigger('click')
    await flushPromises()

    expect(mockUpdateAiConnection).toHaveBeenCalledWith('client-1', 'conn-embed', {
      displayName: 'Embedding Pool',
      endpointUrl: 'https://embeddings.openai.azure.com/',
      models: ['text-embedding-3-large'],
      modelCapabilities: [
        {
          modelName: 'text-embedding-3-large',
          tokenizerName: 'o200k_base',
          maxInputTokens: 16384,
          embeddingDimensions: 3072,
        },
      ],
    })
  })
})
