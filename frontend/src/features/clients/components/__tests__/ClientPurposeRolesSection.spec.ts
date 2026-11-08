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

function select(wrapper: ReturnType<typeof mountSection>, testId: string) {
  return wrapper.find<HTMLSelectElement>(`[data-testid="${testId}"]`)
}

describe('ClientPurposeRolesSection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listEffectiveForClient.mockResolvedValue([
      {
        id: '1',
        name: 'deep',
        capability: 'chat',
        connectionId: 'c',
        configuredModelId: 'm',
        reasoningLevel: 'medium',
        protocolMode: 'Auto',
        scope: 'client',
        referencedModel: { availability: 'available', reasoningLevels: ['low', 'medium', 'high'], defaultReasoningLevel: 'low' },
      },
      {
        id: '2',
        name: 'fast',
        capability: 'chat',
        connectionId: 'c',
        configuredModelId: 'f',
        reasoningLevel: null,
        protocolMode: 'Auto',
        scope: 'client',
        referencedModel: { availability: 'available', reasoningLevels: ['none', 'low'], defaultReasoningLevel: 'none' },
      },
      { id: '3', name: 'embed', capability: 'embedding', connectionId: 'c', configuredModelId: 'e', reasoningLevel: null, protocolMode: 'Embeddings', scope: 'client' },
    ])
    mocks.listPurposeRoles.mockResolvedValue([
      { purpose: 'reviewTriage', logicalModelName: 'deep', reasoningLevel: 'high' },
      { purpose: 'reviewVerification', logicalModelName: 'fast', reasoningLevel: null },
      { purpose: 'reviewHighEffort', logicalModelName: 'fast', reasoningLevel: 'low' },
    ])
    mocks.setPurposeRole.mockResolvedValue(undefined)
    mocks.removePurposeRole.mockResolvedValue(undefined)
  })

  it('shows a row per purpose with the current mapping selected', async () => {
    const wrapper = mountSection()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="purpose-role-row"]').length).toBeGreaterThanOrEqual(9)
    expect(select(wrapper, 'purpose-role-select-reviewTriage').element.value).toBe('deep')
  })

  it('offers chat models to chat purposes and embedding models to the embedding purpose', async () => {
    const wrapper = mountSection()
    await flushPromises()

    const triageOptions = select(wrapper, 'purpose-role-select-reviewTriage')
      .findAll('option')
      .map(option => option.attributes('value'))
    expect(triageOptions).toContain('deep')
    expect(triageOptions).not.toContain('embed')

    const embedOptions = select(wrapper, 'purpose-role-select-embeddingDefault')
      .findAll('option')
      .map(option => option.attributes('value'))
    expect(embedOptions).toContain('embed')
    expect(embedOptions).not.toContain('deep')
  })

  it('sets a mapping without a level when a model is chosen', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await select(wrapper, 'purpose-role-select-reviewDefault').setValue('deep')
    await flushPromises()

    expect(mocks.setPurposeRole).toHaveBeenCalledWith('client-1', 'reviewDefault', 'deep', null)
  })

  it('clears a mapping when the blank option is chosen', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await select(wrapper, 'purpose-role-select-reviewTriage').setValue('')
    await flushPromises()

    expect(mocks.removePurposeRole).toHaveBeenCalledWith('client-1', 'reviewTriage')
  })
})

describe('ClientPurposeRolesSection reasoning level', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.listEffectiveForClient.mockResolvedValue([
      {
        id: '1',
        name: 'deep',
        capability: 'chat',
        reasoningLevel: 'medium',
        referencedModel: { availability: 'available', reasoningLevels: ['low', 'medium', 'high'], defaultReasoningLevel: 'low' },
      },
      {
        id: '2',
        name: 'fast',
        capability: 'chat',
        reasoningLevel: null,
        referencedModel: { availability: 'available', reasoningLevels: ['none', 'low'], defaultReasoningLevel: 'none' },
      },
      { id: '3', name: 'embed', capability: 'embedding', reasoningLevel: null },
    ])
    mocks.listPurposeRoles.mockResolvedValue([
      { purpose: 'reviewTriage', logicalModelName: 'deep', reasoningLevel: 'high' },
      { purpose: 'reviewVerification', logicalModelName: 'fast', reasoningLevel: null },
      { purpose: 'reviewHighEffort', logicalModelName: 'fast', reasoningLevel: 'low' },
      { purpose: 'embeddingDefault', logicalModelName: 'embed' },
    ])
    mocks.setPurposeRole.mockResolvedValue(undefined)
  })

  it('shows the level of each assignment and offers the levels of the model behind its logical model', async () => {
    const wrapper = mountSection()
    await flushPromises()

    const triage = select(wrapper, 'purpose-role-level-reviewTriage')
    expect(triage.element.value).toBe('high')
    expect(triage.findAll('option').map(option => option.attributes('value'))).toEqual(['', 'low', 'medium', 'high'])
    expect(triage.find('option').text()).toBe('Inherit (medium, from the logical model)')

    const verification = select(wrapper, 'purpose-role-level-reviewVerification')
    expect(verification.element.value).toBe('')
    expect(verification.findAll('option').map(option => option.attributes('value'))).toEqual(['', 'none', 'low'])
    expect(verification.find('option').text()).toBe("Inherit (none, from the model's default level)")
  })

  it('offers no level for a purpose without a logical model or for the embedding purpose', async () => {
    const wrapper = mountSection()
    await flushPromises()

    expect(select(wrapper, 'purpose-role-level-reviewDefault').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="purpose-role-level-embeddingDefault"]').exists()).toBe(false)
  })

  it('sends the chosen level with the logical model of the purpose', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await select(wrapper, 'purpose-role-level-reviewTriage').setValue('low')
    await flushPromises()

    expect(mocks.setPurposeRole).toHaveBeenCalledWith('client-1', 'reviewTriage', 'deep', 'low')
  })

  it('sends no level when the purpose is set back to inherit', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await select(wrapper, 'purpose-role-level-reviewTriage').setValue('')
    await flushPromises()

    expect(mocks.setPurposeRole).toHaveBeenCalledWith('client-1', 'reviewTriage', 'deep', null)
  })

  // The save refuses a level the model behind the new logical model does not accept.
  it('keeps the level when the new logical model accepts it and drops it otherwise', async () => {
    const wrapper = mountSection()
    await flushPromises()

    await select(wrapper, 'purpose-role-select-reviewHighEffort').setValue('deep')
    await flushPromises()
    expect(mocks.setPurposeRole).toHaveBeenLastCalledWith('client-1', 'reviewHighEffort', 'deep', 'low')

    await select(wrapper, 'purpose-role-select-reviewTriage').setValue('fast')
    await flushPromises()
    expect(mocks.setPurposeRole).toHaveBeenLastCalledWith('client-1', 'reviewTriage', 'fast', null)
  })

  it("shows the server's reason when it refuses the level", async () => {
    // The server knows the levels of the model when the list this page loaded is out of date.
    mocks.setPurposeRole.mockRejectedValue(new Error("The model 'gpt-6.1-sol' does not accept the reasoning level 'medium'. It accepts low, high."))
    const wrapper = mountSection()
    await flushPromises()

    await select(wrapper, 'purpose-role-level-reviewTriage').setValue('medium')
    await flushPromises()

    expect(wrapper.find('[data-testid="purpose-roles-error"]').text()).toContain("does not accept the reasoning level 'medium'")
  })
})
