// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
      { id: 'chat1', displayName: 'GPT', remoteModelId: 'gpt-4o', supportsChat: true, supportsEmbedding: false },
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
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningEffort: 'high', protocolMode: 'auto', scope: 'tenant' },
      { id: '2', name: 'wide', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningEffort: 'none', protocolMode: 'auto', scope: 'tenant' },
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
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningEffort: 'none', protocolMode: 'auto', scope: 'tenant' },
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
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningEffort: 'high', protocolMode: 'auto', scope: 'tenant' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    const row = wrapper.find('[data-testid="tenant-logical-model-row"]')
    expect(row.text()).toContain('Azure') // connection display name resolved from the id
    expect(row.text()).toContain('GPT') // model display name resolved from the id
  })

  it('edits an entry through the update endpoint', async () => {
    mocks.listTenantCatalog.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'chat1', reasoningEffort: 'high', protocolMode: 'auto', scope: 'tenant' },
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
