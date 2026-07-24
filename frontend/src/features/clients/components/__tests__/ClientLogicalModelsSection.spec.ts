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
    configuredModels: [{ id: 'm1', displayName: 'GPT', supportsChat: true }],
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
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningEffort: 'high', protocolMode: 'responses', scope: 'client' },
      { id: '2', name: 'wide', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningEffort: 'none', protocolMode: 'auto', scope: 'tenant' },
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
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningEffort: 'high', protocolMode: 'auto', scope: 'client' },
    ])

    const wrapper = mountSection()
    await flushPromises()

    const row = wrapper.find('[data-testid="logical-model-row"]')
    expect(row.text()).toContain('Azure') // connection display name resolved from the id
    expect(row.text()).toContain('GPT') // configured-model display name resolved from the id
  })

  it('edits an override through the update endpoint', async () => {
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningEffort: 'high', protocolMode: 'auto', scope: 'client' },
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
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c1', configuredModelId: 'm1', reasoningEffort: 'none', protocolMode: 'auto', scope: 'client' },
    ])
    mocks.deleteClientOverride.mockResolvedValue(undefined)

    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="logical-model-delete"]').trigger('click')
    await flushPromises()

    expect(mocks.deleteClientOverride).toHaveBeenCalledWith('client-1', 'deep')
  })
})
