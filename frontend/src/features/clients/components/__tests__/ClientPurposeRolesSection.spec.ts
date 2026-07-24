// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ClientPurposeRolesSection from '../ClientPurposeRolesSection.vue'

const mocks = vi.hoisted(() => ({
  listEffectiveForClient: vi.fn(),
  listPurposeRoles: vi.fn(),
  setPurposeRole: vi.fn(),
  removePurposeRole: vi.fn(),
}))

vi.mock('@/services/logicalModelsService', () => mocks)

function mountSection() {
  return mount(ClientPurposeRolesSection, { props: { clientId: 'client-1' } })
}

describe('ClientPurposeRolesSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listEffectiveForClient.mockResolvedValue([
      { id: '1', name: 'deep', capability: 'chat', connectionId: 'c', configuredModelId: 'm', reasoningEffort: 'high', protocolMode: 'auto', scope: 'client' },
      { id: '2', name: 'embed', capability: 'embedding', connectionId: 'c', configuredModelId: 'm', reasoningEffort: 'none', protocolMode: 'embeddings', scope: 'client' },
    ])
    mocks.listPurposeRoles.mockResolvedValue([{ purpose: 'reviewTriage', logicalModelName: 'deep' }])
  })

  it('shows a row per purpose with the current mapping selected', async () => {
    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="purpose-role-row"]').length).toBeGreaterThanOrEqual(9)
    const triage = wrapper.find('[data-testid="purpose-role-select-reviewTriage"]')
    expect((triage.element as HTMLSelectElement).value).toBe('deep')
  })

  it('offers chat models to chat purposes and embedding models to the embedding purpose', async () => {
    const wrapper = mountSection()
    await flushPromises()

    const triageOptions = wrapper.find('[data-testid="purpose-role-select-reviewTriage"]')
      .findAll('option')
      .map(option => option.attributes('value'))
    expect(triageOptions).toContain('deep')
    expect(triageOptions).not.toContain('embed')

    const embedOptions = wrapper.find('[data-testid="purpose-role-select-embeddingDefault"]')
      .findAll('option')
      .map(option => option.attributes('value'))
    expect(embedOptions).toContain('embed')
    expect(embedOptions).not.toContain('deep')
  })

  it('sets a mapping when a model is chosen', async () => {
    mocks.setPurposeRole.mockResolvedValue(undefined)
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="purpose-role-select-reviewDefault"]').setValue('deep')
    await flushPromises()

    expect(mocks.setPurposeRole).toHaveBeenCalledWith('client-1', 'reviewDefault', 'deep')
  })

  it('clears a mapping when the blank option is chosen', async () => {
    mocks.removePurposeRole.mockResolvedValue(undefined)
    const wrapper = mountSection()
    await flushPromises()

    await wrapper.find('[data-testid="purpose-role-select-reviewTriage"]').setValue('')
    await flushPromises()

    expect(mocks.removePurposeRole).toHaveBeenCalledWith('client-1', 'reviewTriage')
  })
})
