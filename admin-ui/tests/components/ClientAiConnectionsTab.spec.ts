// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const mockListAiConnections = vi.fn()
const mockCreateAiConnection = vi.fn()
const mockUpdateAiConnection = vi.fn()
const mockDeleteAiConnection = vi.fn()
const mockActivateAiConnection = vi.fn()
const mockDeactivateAiConnection = vi.fn()
const mockVerifyAiConnection = vi.fn()
const mockDiscoverAiModels = vi.fn()

vi.mock('@/services/aiConnectionsService', () => ({
  listAiConnections: mockListAiConnections,
  createAiConnection: mockCreateAiConnection,
  updateAiConnection: mockUpdateAiConnection,
  deleteAiConnection: mockDeleteAiConnection,
  activateAiConnection: mockActivateAiConnection,
  deactivateAiConnection: mockDeactivateAiConnection,
  verifyAiConnection: mockVerifyAiConnection,
  discoverAiModels: mockDiscoverAiModels,
}))

vi.mock('@/components/ConfirmDialog.vue', () => ({
  default: {
    name: 'ConfirmDialog',
    props: ['open', 'message'],
    emits: ['confirm', 'cancel'],
    template: '<div class="confirm-dialog-stub" />',
  },
}))

const sampleProfile = {
  id: 'conn-1',
  clientId: 'client-1',
  displayName: 'Primary OpenAI',
  providerKind: 'openAi',
  baseUrl: 'https://api.openai.com/v1',
  authMode: 'apiKey',
  discoveryMode: 'providerCatalog',
  isActive: false,
  configuredModels: [
    {
      id: 'chat-model-id',
      remoteModelId: 'gpt-4.1-mini',
      displayName: 'GPT-4.1 Mini',
      operationKinds: ['chat'],
      supportedProtocolModes: ['auto', 'responses', 'chatCompletions'],
      supportsStructuredOutput: true,
      supportsToolUse: true,
      supportsChat: true,
      supportsEmbedding: false,
      source: 'manual',
    },
  ],
  purposeBindings: [
    { id: 'binding-default', purpose: 'reviewDefault', configuredModelId: 'chat-model-id', remoteModelId: 'gpt-4.1-mini', protocolMode: 'auto', isEnabled: true },
  ],
  verification: {
    status: 'verified',
    summary: 'Verified against the provider catalog.',
  },
}

describe('ClientAiConnectionsTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockListAiConnections.mockResolvedValue([sampleProfile])
    mockCreateAiConnection.mockResolvedValue(sampleProfile)
    mockUpdateAiConnection.mockResolvedValue(sampleProfile)
    mockDeleteAiConnection.mockResolvedValue(undefined)
    mockActivateAiConnection.mockResolvedValue({ ...sampleProfile, isActive: true })
    mockDeactivateAiConnection.mockResolvedValue({ ...sampleProfile, isActive: false })
    mockVerifyAiConnection.mockResolvedValue({ status: 'verified', summary: 'Verified against the provider catalog.' })
    mockDiscoverAiModels.mockResolvedValue({ discoveryStatus: 'succeeded', manualEntryAllowed: true, warnings: [], models: [] })
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and renders existing AI profiles on mount', async () => {
    const { default: ClientAiConnectionsTab } = await import('@/components/ClientAiConnectionsTab.vue')
    const wrapper = mount(ClientAiConnectionsTab, { props: { clientId: 'client-1' } })
    await flushPromises()

    expect(mockListAiConnections).toHaveBeenCalledWith('client-1')
    expect(wrapper.text()).toContain('Primary OpenAI')
    expect(wrapper.text()).toContain('Verified')
  })

  it('uses list-first navigation and opens edit details only after selecting a profile', async () => {
    const { default: ClientAiConnectionsTab } = await import('@/components/ClientAiConnectionsTab.vue')
    const wrapper = mount(ClientAiConnectionsTab, { props: { clientId: 'client-1' } })
    await flushPromises()

    expect(wrapper.find('.ai-profile-card').exists()).toBe(true)
    expect(wrapper.find('.ai-editor-card').exists()).toBe(false)

    await wrapper.find('.ai-profile-card').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Edit AI Provider')
    expect(wrapper.text()).toContain('Back to list')
  })

  it('keeps advanced request overrides collapsed until explicitly opened', async () => {
    const { default: ClientAiConnectionsTab } = await import('@/components/ClientAiConnectionsTab.vue')
    const wrapper = mount(ClientAiConnectionsTab, { props: { clientId: 'client-1' } })
    await flushPromises()

    const addProfileButton = wrapper.findAll('button').find((button) => button.text() === 'Add Profile')
    expect(addProfileButton).toBeDefined()
    await addProfileButton!.trigger('click')
    await flushPromises()

    const details = wrapper.find('.advanced-settings-details')
    expect(details.exists()).toBe(true)
    
    // In details/summary elements the content is in the DOM but typically hidden.
    // We check the 'open' property instead of text presence.
    expect((details.element as HTMLDetailsElement).open).toBe(false)

    const summary = wrapper.find('.advanced-settings-summary')
    expect(summary.exists()).toBe(true)
    
    // Simulate toggle since JSDOM might not natively toggle details state on click properly in all versions
    await summary.trigger('click')
    
    // and manually fire toggle event which our vue component listens to
    const detailsEl = details.element as HTMLDetailsElement
    detailsEl.open = true
    await details.trigger('toggle')
    await flushPromises()

    expect(wrapper.vm.advancedSettingsOpen).toBe(true)
  })

  it('creates a provider-neutral AI profile with chat and embedding purpose bindings', async () => {
    const randomIds = ['chat-local', 'embed-local']
    vi.spyOn(globalThis.crypto, 'randomUUID').mockImplementation(() => randomIds.shift() ?? 'fallback-local')
    mockListAiConnections.mockResolvedValue([])

    const { default: ClientAiConnectionsTab } = await import('@/components/ClientAiConnectionsTab.vue')
    const wrapper = mount(ClientAiConnectionsTab, { props: { clientId: 'client-1' } })
    await flushPromises()

    await wrapper.find('[data-testid="ai-display-name"]').setValue('Unified OpenAI Stack')
    await wrapper.find('[data-testid="ai-provider-kind"]').setValue('openAi')
    await wrapper.find('[data-testid="ai-base-url"]').setValue('https://api.openai.com/v1')
    await wrapper.find('[data-testid="ai-auth-mode"]').setValue('apiKey')
    await wrapper.find('[data-testid="ai-api-key"]').setValue('secret-key')

    const addModelButton = wrapper.findAll('button').find((button) => button.text().includes('Add Model'))
    expect(addModelButton).toBeDefined()
    
    // Add first model
    await addModelButton!.trigger('click')
    await flushPromises()

    let modelRows = wrapper.findAll('.ai-model-row')
    expect(modelRows).toHaveLength(1)

    // Fill first model
    await wrapper.find('[data-testid="ai-model-id-0"]').setValue('gpt-4.1-mini')
    await modelRows[0].findAll('input[type="text"]')[1].setValue('GPT-4.1 Mini')
    
    // Save first model edit state
    await modelRows[0].findAll('button').find(b => b.text() === 'Done')?.trigger('click')
    await flushPromises()
    
    // Add second model
    await addModelButton!.trigger('click')
    await flushPromises()
    
    modelRows = wrapper.findAll('.ai-model-row')
    expect(modelRows).toHaveLength(2)

    await wrapper.find('[data-testid="ai-model-id-1"]').setValue('text-embedding-3-large')
    await modelRows[1].findAll('input[type="text"]')[1].setValue('Text Embedding 3 Large')
    await modelRows[1].find('select').setValue('embedding')
    await flushPromises()
    
    // After changing to embedding, tokenizer inputs appear
    const embeddingInputs = modelRows[1].findAll('input')
    // remoteId, displayName, tokenizer, maxTokens, dimensions
    await embeddingInputs[2].setValue('cl100k_base')
    await embeddingInputs[3].setValue('8192')
    await embeddingInputs[4].setValue('3072')

    // Save second model edit state
    await modelRows[1].findAll('button').find(b => b.text() === 'Done')?.trigger('click')
    await flushPromises()

    const bindingRows = wrapper.findAll('.ai-binding-row')
    expect(bindingRows).toHaveLength(6)
    
    // Checkboxes inside custom component wrappers or raw HTML inputs can sometimes be flaky to toggle in mount,
    // so let's directly edit the data model or ensure the correct input is bound if needed. 
    // Since we just need to verify the model binding works, we'll select models for chat and embed.
    for (const [index, row] of bindingRows.entries()) {
      const selects = row.findAll('select')
      await selects[0].setValue(index === 5 ? 'embed-local' : 'chat-local')
    }

    const saveButton = wrapper.findAll('button').find((button) => button.text().includes('Create Profile'))
    expect(saveButton).toBeDefined()
    await saveButton!.trigger('click')
    await flushPromises()

    expect(mockCreateAiConnection).toHaveBeenCalledWith('client-1', {
      displayName: 'Unified OpenAI Stack',
      providerKind: 'openAi',
      baseUrl: 'https://api.openai.com/v1',
      auth: {
        mode: 'apiKey',
        apiKey: 'secret-key',
      },
      discoveryMode: 'providerCatalog',
      defaultHeaders: undefined,
      defaultQueryParams: undefined,
      configuredModels: [
        {
          id: undefined,
          remoteModelId: 'gpt-4.1-mini',
          displayName: 'GPT-4.1 Mini',
          operationKinds: ['chat'],
          supportedProtocolModes: ['auto', 'responses', 'chatCompletions'],
          tokenizerName: undefined,
          maxInputTokens: undefined,
          embeddingDimensions: undefined,
          supportsStructuredOutput: true,
          supportsToolUse: true,
          source: 'manual',
        },
        {
          id: undefined,
          remoteModelId: 'text-embedding-3-large',
          displayName: 'Text Embedding 3 Large',
          operationKinds: ['embedding'],
          supportedProtocolModes: ['auto', 'embeddings'],
          tokenizerName: 'cl100k_base',
          maxInputTokens: 8192,
          embeddingDimensions: 3072,
          supportsStructuredOutput: false,
          supportsToolUse: false,
          source: 'manual',
        },
      ],
      purposeBindings: [
        { id: undefined, purpose: 'reviewDefault', configuredModelId: undefined, remoteModelId: 'gpt-4.1-mini', protocolMode: 'auto', isEnabled: true },
        { id: undefined, purpose: 'reviewLowEffort', configuredModelId: undefined, remoteModelId: 'gpt-4.1-mini', protocolMode: 'auto', isEnabled: true },
        { id: undefined, purpose: 'reviewMediumEffort', configuredModelId: undefined, remoteModelId: 'gpt-4.1-mini', protocolMode: 'auto', isEnabled: true },
        { id: undefined, purpose: 'reviewHighEffort', configuredModelId: undefined, remoteModelId: 'gpt-4.1-mini', protocolMode: 'auto', isEnabled: true },
        { id: undefined, purpose: 'memoryReconsideration', configuredModelId: undefined, remoteModelId: 'gpt-4.1-mini', protocolMode: 'auto', isEnabled: true },
        { id: undefined, purpose: 'embeddingDefault', configuredModelId: undefined, remoteModelId: 'text-embedding-3-large', protocolMode: 'embeddings', isEnabled: true },
      ],
    })
  })

  it('verifies and activates an existing profile without sending a model body', async () => {
    const { default: ClientAiConnectionsTab } = await import('@/components/ClientAiConnectionsTab.vue')
    const wrapper = mount(ClientAiConnectionsTab, { props: { clientId: 'client-1' } })
    await flushPromises()

    const verifyButton = wrapper.findAll('button').find((button) => button.text() === 'Verify')
    const activateButton = wrapper.findAll('button').find((button) => button.text() === 'Activate')

    expect(verifyButton).toBeDefined()
    expect(activateButton).toBeDefined()

    await verifyButton!.trigger('click')
    await flushPromises()
    await activateButton!.trigger('click')
    await flushPromises()

    expect(mockVerifyAiConnection).toHaveBeenCalledWith('client-1', 'conn-1')
    expect(mockActivateAiConnection).toHaveBeenCalledWith('client-1', 'conn-1')
  })
})