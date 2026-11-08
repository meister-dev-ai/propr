// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import TenantLogicalModelsSection from '../TenantLogicalModelsSection.vue'

const mocks = vi.hoisted(() => ({
  listTenantCatalog: vi.fn(),
  listTenantConnections: vi.fn(),
  createTenantEntry: vi.fn(),
  updateTenantEntry: vi.fn(),
  deleteTenantEntry: vi.fn(),
}))

vi.mock('@/services/logicalModelsService', () => mocks)

const connections = [
  {
    id: 'c1',
    displayName: 'Azure',
    isActive: true,
    configuredModels: [
      { id: 'chat1', displayName: 'GPT', remoteModelId: 'gpt-4o', supportsChat: true, supportsEmbedding: false, reasoningLevels: ['none', 'low', 'high'], defaultReasoningLevel: null },
      { id: 'chat2', displayName: 'Mini', remoteModelId: 'gpt-4o-mini', supportsChat: true, supportsEmbedding: false, reasoningLevels: ['low'], defaultReasoningLevel: 'low' },
      { id: 'emb1', displayName: 'Embed', remoteModelId: 'text-embed', supportsChat: false, supportsEmbedding: true },
    ],
  },
]

function mountSection() {
  return mount(TenantLogicalModelsSection, { props: { tenantId: 'tenant-1' } })
}

describe('TenantLogicalModelsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listTenantCatalog.mockResolvedValue([])
    mocks.listTenantConnections.mockResolvedValue(connections)
  })

  it('renders a row per tenant-catalog entry', async () => {
    mocks.listTenantCatalog.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'tenant' },
      { id: '2', name: 'wide', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningLevel: null, protocolMode: 'Auto', scope: 'tenant' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="tenant-logical-model-row"]')).toHaveLength(2)
    expect(wrapper.text()).toContain('deep')
    expect(wrapper.text()).toContain('wide')
  })

  it('lists the tenant connections and filters models by the chosen capability', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-logical-model-add"]').trigger('click')

    // Connection option shows the tenant connection's display name.
    expect(wrapper.find('[data-testid="tenant-logical-model-connection"]').text()).toContain('Azure')

    await wrapper.find('[data-testid="tenant-logical-model-connection"]').setValue('c1')

    // Chat capability (default) offers only the chat model.
    let modelValues = wrapper
      .find('[data-testid="tenant-logical-model-model"]')
      .findAll('option')
      .map(option => option.attributes('value'))
    expect(modelValues).toContain('chat1')
    expect(modelValues).not.toContain('emb1')

    // Switching to embedding offers only the embedding model.
    await wrapper.find('[data-testid="tenant-logical-model-capability"]').setValue('embedding')
    modelValues = wrapper
      .find('[data-testid="tenant-logical-model-model"]')
      .findAll('option')
      .map(option => option.attributes('value'))
    expect(modelValues).toContain('emb1')
    expect(modelValues).not.toContain('chat1')
  })

  it('creates a tenant-catalog entry from the form and reloads', async () => {
    mocks.createTenantEntry.mockResolvedValue({})
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-logical-model-add"]').trigger('click')
    await wrapper.find('[data-testid="tenant-logical-model-name"]').setValue('  deep  ')
    await wrapper.find('[data-testid="tenant-logical-model-connection"]').setValue('c1')
    await wrapper.find('[data-testid="tenant-logical-model-model"]').setValue('chat1')
    await wrapper.find('[data-testid="tenant-logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createTenantEntry).toHaveBeenCalledWith(
      'tenant-1',
      expect.objectContaining({ name: 'deep', connectionId: 'c1', configuredModelId: 'chat1', capability: 'chat' }),
    )
    // Reloaded after create (initial mount + post-create).
    expect(mocks.listTenantCatalog).toHaveBeenCalledTimes(2)
  })

  it('deletes a tenant-catalog entry', async () => {
    mocks.listTenantCatalog.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningLevel: null, protocolMode: 'Auto', scope: 'tenant' },
    ])
    mocks.deleteTenantEntry.mockResolvedValue(undefined)

    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-logical-model-delete"]').trigger('click')
    await flushPromises()

    expect(mocks.deleteTenantEntry).toHaveBeenCalledWith('tenant-1', 'deep')
  })

  it('shows the connection and model for each entry', async () => {
    mocks.listTenantCatalog.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'tenant' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    const row = wrapper.find('[data-testid="tenant-logical-model-row"]')
    expect(row.text()).toContain('Azure') // connection display name resolved from the id
    expect(row.text()).toContain('GPT') // model display name resolved from the id
  })

  it('edits an entry through the update endpoint', async () => {
    mocks.listTenantCatalog.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'tenant' },
    ])
    mocks.updateTenantEntry.mockResolvedValue(undefined)

    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="tenant-logical-model-edit"]').trigger('click')
    await wrapper.find('[data-testid="tenant-logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.updateTenantEntry).toHaveBeenCalledWith(
      'tenant-1',
      'deep',
      expect.objectContaining({ connectionId: 'c1', configuredModelId: 'chat1', capability: 'chat' }),
    )
    expect(mocks.createTenantEntry).not.toHaveBeenCalled()
  })
})

describe('TenantLogicalModelsSection reasoning level', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listTenantCatalog.mockResolvedValue([])
    mocks.listTenantConnections.mockResolvedValue(connections)
    mocks.createTenantEntry.mockResolvedValue({})
  })

  async function openCreateWithModel(wrapper: ReturnType<typeof mountSection>, modelId: string) {
    await wrapper.find('[data-testid="tenant-logical-model-add"]').trigger('click')
    await wrapper.find('[data-testid="tenant-logical-model-name"]').setValue('deep')
    await wrapper.find('[data-testid="tenant-logical-model-connection"]').setValue('c1')
    await wrapper.find('[data-testid="tenant-logical-model-model"]').setValue(modelId)
  }

  it('shows the level of each entry, and that an entry without one inherits a level', async () => {
    mocks.listTenantCatalog.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'tenant' },
      { id: '2', name: 'fast', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat2', reasoningLevel: null, protocolMode: 'Auto', scope: 'tenant' },
      { id: '3', name: 'embed', capability: 'embedding', connectionId: 'c1', configuredModelId: 'emb1', reasoningLevel: null, protocolMode: 'Embeddings', scope: 'tenant' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="tenant-logical-model-level-cell"]').map(cell => cell.text())).toEqual([
      'high',
      'Inherit',
      'Not applicable',
    ])
  })

  it('offers the levels of the selected model, inherits its default level and saves the chosen level', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'chat2')
    const options = wrapper.find('[data-testid="tenant-logical-model-level"]').findAll('option')
    expect(options.map(option => option.attributes('value'))).toEqual(['', 'low'])
    expect(options[0]!.text()).toBe("Inherit (low, from the model's default level)")

    await wrapper.find('[data-testid="tenant-logical-model-model"]').setValue('chat1')
    await wrapper.find('[data-testid="tenant-logical-model-level"]').setValue('high')
    await wrapper.find('[data-testid="tenant-logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createTenantEntry).toHaveBeenCalledWith('tenant-1', expect.objectContaining({ configuredModelId: 'chat1', reasoningLevel: 'high' }))
  })

  it('clears a level the newly selected model does not accept', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'chat1')
    await wrapper.find('[data-testid="tenant-logical-model-level"]').setValue('high')
    await wrapper.find('[data-testid="tenant-logical-model-model"]').setValue('chat2')
    await wrapper.find('[data-testid="tenant-logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createTenantEntry).toHaveBeenCalledWith('tenant-1', expect.objectContaining({ configuredModelId: 'chat2', reasoningLevel: null }))
  })

  it('offers no level for an embedding entry and saves none', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'chat1')
    await wrapper.find('[data-testid="tenant-logical-model-level"]').setValue('high')
    await wrapper.find('[data-testid="tenant-logical-model-capability"]').setValue('embedding')
    await wrapper.find('[data-testid="tenant-logical-model-model"]').setValue('emb1')

    expect(wrapper.find('[data-testid="tenant-logical-model-level"]').exists()).toBe(false)

    await wrapper.find('[data-testid="tenant-logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createTenantEntry).toHaveBeenCalledWith('tenant-1', expect.objectContaining({ capability: 'embedding', reasoningLevel: null }))
  })
})
