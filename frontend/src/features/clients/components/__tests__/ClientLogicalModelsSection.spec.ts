// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import ClientLogicalModelsSection from '../ClientLogicalModelsSection.vue'

const mocks = vi.hoisted(() => ({
  listEffectiveForClient: vi.fn(),
  createClientOverride: vi.fn(),
  updateClientOverride: vi.fn(),
  deleteClientOverride: vi.fn(),
}))

vi.mock('@/services/logicalModelsService', () => mocks)

const connections = [
  {
    id: 'c1',
    displayName: 'Azure',
    configuredModels: [
      { id: 'm1', displayName: 'GPT', supportsChat: true, reasoningLevels: ['low', 'medium', 'high'], defaultReasoningLevel: 'medium' },
      { id: 'm2', displayName: 'Mini', supportsChat: true, reasoningLevels: ['low'], defaultReasoningLevel: null },
      { id: 'e1', displayName: 'Embed', supportsChat: false, supportsEmbedding: true },
    ],
  },
] as unknown as AiConnectionDto[]

function mountSection() {
  return mount(ClientLogicalModelsSection, { props: { clientId: 'client-1', connections } })
}

describe('ClientLogicalModelsSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listEffectiveForClient.mockResolvedValue([])
  })

  it('renders effective logical models tagged by scope', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: 'high', protocolMode: 'responses', scope: 'client' },
      { id: '2', name: 'wide', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: null, protocolMode: 'Auto', scope: 'tenant' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    const rows = wrapper.findAll('[data-testid="logical-model-row"]')
    expect(rows).toHaveLength(2)
    expect(wrapper.text()).toContain('deep')
    expect(wrapper.text()).toContain('wide')
    // Only the client-scoped override is deletable.
    expect(wrapper.findAll('[data-testid="logical-model-delete"]')).toHaveLength(1)
  })

  it('creates a client override from the form and reloads', async () => {
    mocks.createClientOverride.mockResolvedValue({})
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="logical-model-add"]').trigger('click')
    await wrapper.find('[data-testid="logical-model-name"]').setValue('deep')
    await wrapper.find('[data-testid="logical-model-connection"]').setValue('c1')
    await wrapper.find('[data-testid="logical-model-model"]').setValue('m1')
    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createClientOverride).toHaveBeenCalledWith(
      'client-1',
      expect.objectContaining({ name: 'deep', connectionId: 'c1', configuredModelId: 'm1', capability: 'chat' }),
    )
    // Reloaded after create (initial mount + post-create).
    expect(mocks.listEffectiveForClient).toHaveBeenCalledTimes(2)
  })

  it('shows the connection and model for each logical model', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'client' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    const row = wrapper.find('[data-testid="logical-model-row"]')
    expect(row.text()).toContain('Azure') // connection display name resolved from the id
    expect(row.text()).toContain('GPT') // configured-model display name resolved from the id
  })

  it('edits an override through the update endpoint', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'client' },
    ])
    mocks.updateClientOverride.mockResolvedValue(undefined)

    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="logical-model-edit"]').trigger('click')
    // The form is prefilled from the row; saving calls the update endpoint keyed by the (immutable) name.
    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.updateClientOverride).toHaveBeenCalledWith(
      'client-1',
      'deep',
      expect.objectContaining({ connectionId: 'c1', configuredModelId: 'm1', capability: 'chat' }),
    )
    expect(mocks.createClientOverride).not.toHaveBeenCalled()
  })

  it('deletes a client-scoped override', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: null, protocolMode: 'Auto', scope: 'client' },
    ])
    mocks.deleteClientOverride.mockResolvedValue(undefined)

    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="logical-model-delete"]').trigger('click')
    await flushPromises()

    expect(mocks.deleteClientOverride).toHaveBeenCalledWith('client-1', 'deep')
  })
})

describe('ClientLogicalModelsSection reasoning level', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listEffectiveForClient.mockResolvedValue([])
    mocks.createClientOverride.mockResolvedValue({})
    mocks.updateClientOverride.mockResolvedValue(undefined)
  })

  async function openCreateWithModel(wrapper: ReturnType<typeof mountSection>, modelId: string) {
    await wrapper.find('[data-testid="logical-model-add"]').trigger('click')
    await wrapper.find('[data-testid="logical-model-name"]').setValue('deep')
    await wrapper.find('[data-testid="logical-model-connection"]').setValue('c1')
    await wrapper.find('[data-testid="logical-model-model"]').setValue(modelId)
  }

  it('shows the level of each logical model, and that a logical model without one inherits a level', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: 'high', protocolMode: 'Auto', scope: 'client' },
      { id: '2', name: 'fast', capability: 'chat', connectionId: 'c1', configuredModelId: 'm2', reasoningLevel: null, protocolMode: 'Auto', scope: 'client' },
      { id: '3', name: 'embed', capability: 'embedding', connectionId: 'c1', configuredModelId: 'e1', reasoningLevel: null, protocolMode: 'Embeddings', scope: 'client' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="logical-model-level-cell"]').map(cell => cell.text())).toEqual([
      'high',
      'Inherit',
      'Not applicable',
    ])
  })

  it('offers the levels of the selected model and inherits its default level', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'm1')

    const options = wrapper.find('[data-testid="logical-model-level"]').findAll('option')
    expect(options.map(option => option.attributes('value'))).toEqual(['', 'low', 'medium', 'high'])
    expect(options[0]!.text()).toBe("Inherit (medium, from the model's default level)")
  })

  it('saves the chosen level', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'm1')
    await wrapper.find('[data-testid="logical-model-level"]').setValue('high')
    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createClientOverride).toHaveBeenCalledWith('client-1', expect.objectContaining({ name: 'deep', reasoningLevel: 'high' }))
  })

  // The save refuses a level the model does not accept, so choosing another model clears such a level.
  it('clears a level the newly selected model does not accept', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'm1')
    await wrapper.find('[data-testid="logical-model-level"]').setValue('high')
    await wrapper.find('[data-testid="logical-model-model"]').setValue('m2')
    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createClientOverride).toHaveBeenCalledWith('client-1', expect.objectContaining({ configuredModelId: 'm2', reasoningLevel: null }))
  })

  // An embedding model takes no reasoning level, and the save refuses one.
  it('offers no level for an embedding logical model and saves none', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'm1')
    await wrapper.find('[data-testid="logical-model-level"]').setValue('high')
    await wrapper.find('[data-testid="logical-model-capability"]').setValue('embedding')
    await wrapper.find('[data-testid="logical-model-model"]').setValue('e1')

    expect(wrapper.find('[data-testid="logical-model-level"]').exists()).toBe(false)

    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.createClientOverride).toHaveBeenCalledWith('client-1', expect.objectContaining({ capability: 'embedding', reasoningLevel: null }))
  })

  it('opens an override on its stored level and saves it unchanged', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningLevel: 'low', protocolMode: 'Auto', scope: 'client' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="logical-model-edit"]').trigger('click')
    expect((wrapper.find('[data-testid="logical-model-level"]').element as HTMLSelectElement).value).toBe('low')

    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(mocks.updateClientOverride).toHaveBeenCalledWith('client-1', 'deep', expect.objectContaining({ reasoningLevel: 'low' }))
  })

  it("shows the server's reason when it refuses the save", async () => {
    mocks.createClientOverride.mockRejectedValue(
      new Error("Logical model 'deep' cannot be saved: The model 'gpt' does not accept the reasoning level 'max'. It accepts low, medium, high."),
    )
    const wrapper = mountSection()
    await flushPromises()

    await openCreateWithModel(wrapper, 'm1')
    await wrapper.find('[data-testid="logical-model-create-form"]').trigger('submit')
    await flushPromises()

    expect(wrapper.find('[data-testid="logical-model-error"]').text()).toContain("does not accept the reasoning level 'max'")
  })
})
