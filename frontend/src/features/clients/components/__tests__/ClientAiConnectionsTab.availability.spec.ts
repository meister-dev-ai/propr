// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import ClientAiConnectionsTab from '../ClientAiConnectionsTab.vue'

const connections = vi.hoisted(() => ({
  listAiConnections: vi.fn(),
  createAiConnection: vi.fn(),
  updateAiConnection: vi.fn(),
  discoverAiModels: vi.fn(),
  probeAiConnection: vi.fn(),
  listPermittedProviders: vi.fn(),
  verifyAiConnection: vi.fn(),
  activateAiConnection: vi.fn(),
  deactivateAiConnection: vi.fn(),
  deleteAiConnection: vi.fn(),
}))

vi.mock('@/services/aiConnectionsService', () => connections)
vi.mock('@/services/logicalModelsService', () => ({ listEffectiveForClient: vi.fn().mockResolvedValue([]) }))
vi.mock('@/services/modelCatalogService', () => ({
  listProviders: vi.fn().mockResolvedValue([]),
  listModels: vi.fn().mockResolvedValue([]),
}))

const workingProfile = {
  id: 'p-working',
  displayName: 'Azure (prod)',
  providerKind: 'azureOpenAi',
  baseUrl: 'https://prod.openai.azure.com/',
  authMode: 'azureOpenAi:ApiKey',
  isActive: false,
  configuredModels: [],
  purposeBindings: [],
  verification: { status: 'verified' },
  availability: { state: 'available', unresolvedValues: [] },
} as unknown as AiConnectionDto

const quarantinedProfile = {
  id: 'p-quarantined',
  displayName: 'Contoso (prod)',
  providerKind: 'azureOpenAi',
  baseUrl: 'https://contoso.example.com/v1',
  authMode: 'azureOpenAi:ApiKey',
  isActive: false,
  configuredModels: [],
  purposeBindings: [],
  verification: { status: 'verified' },
  availability: {
    state: 'unavailable',
    reason: 'providerFamilyAbsent',
    providerIdentity: 'ContosoLlm',
    unresolvedValues: [],
  },
} as unknown as AiConnectionDto

function mountTab() {
  return mount(ClientAiConnectionsTab, {
    props: { clientId: 'client-1', active: true },
    global: {
      stubs: {
        ClientLogicalModelsSection: true,
        ClientPurposeRolesSection: true,
        ClientReviewPassesEditor: true,
        ModelCatalogPicker: true,
        teleport: true,
      },
    },
  })
}

describe('an unavailable connection in the client connections list', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    connections.listAiConnections.mockResolvedValue([workingProfile, quarantinedProfile])
    connections.listPermittedProviders.mockResolvedValue({
      providers: [
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'azureOpenAi:ApiKey': [{ name: 'apiKey', label: 'API key', isSecret: true, isRequired: true }] },
        },
      ],
      isRestricted: false,
    })
  })

  // The row stays in the list, marked, with what is wrong and what to do about it. Dropping it would hide a
  // configured profile from the operator who has to fix it.
  it('marks the row and states the reason and the remedy, leaving the other row untouched', async () => {
    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.text()).toContain('Azure (prod)')
    expect(wrapper.text()).toContain('Contoso (prod)')
    expect(wrapper.findAll('[data-testid="ai-provider-unavailable"]')).toHaveLength(1)

    const notes = wrapper.findAll('[data-testid="ai-unavailable-note"]')
    expect(notes).toHaveLength(1)
    expect(notes[0].text()).toContain('ContosoLlm')
    expect(notes[0].text()).toContain('Install')
  })

  // Verifying reaches a provider and activating puts the profile in front of a review, and neither can work
  // while the family is missing. Editing and deleting are what is left to do about it.
  it('offers neither verify nor activate on the unavailable row, and still offers delete', async () => {
    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.findAll('[data-testid="ai-verify"]')).toHaveLength(1)
    expect(wrapper.findAll('[data-testid="ai-activate"]')).toHaveLength(1)
    expect(wrapper.findAll('[data-testid="ai-delete"]')).toHaveLength(2)
  })

  // An operator who opened the profile from a link reads it here and nowhere else.
  it('repeats the reason and the remedy in the detail view, with the same actions withheld', async () => {
    const wrapper = mountTab()
    await flushPromises()

    const cards = wrapper.findAll('.ai-profile-card')
    await cards[1].trigger('click')
    await flushPromises()

    const note = wrapper.find('[data-testid="ai-unavailable-note-detail"]')
    expect(note.exists()).toBe(true)
    expect(note.text()).toContain('ContosoLlm')
    expect(note.text()).toContain('Install')
    expect(wrapper.find('[data-testid="ai-verify-detail"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="ai-activate-detail"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="ai-delete-detail"]').exists()).toBe(true)
  })
})
