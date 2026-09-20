// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiConnectionDto, AiDeclaredFieldDto } from '@/services/aiConnectionsService'
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
  ApiFieldValidationError: class extends Error {},
}))

vi.mock('@/services/aiConnectionsService', () => connections)
vi.mock('@/services/logicalModelsService', () => ({ listEffectiveForClient: vi.fn().mockResolvedValue([]) }))
vi.mock('@/services/modelCatalogService', () => ({
  listProviders: vi.fn().mockResolvedValue([]),
  listModels: vi.fn().mockResolvedValue([]),
}))

// One field of every shape the closed vocabulary carries, from a family this build has no knowledge of.
const declaredFields: AiDeclaredFieldDto[] = [
  { name: 'endpoint', label: 'Endpoint', kind: 'url', isRequired: true, isSecret: false, isComputed: false },
  { name: 'region', label: 'Region', kind: 'string', isRequired: false, isSecret: false, isComputed: false },
  { name: 'clientSecret', label: 'Client secret', kind: 'secret', isRequired: false, isSecret: true, isComputed: false },
  { name: 'useCache', label: 'Use cache', kind: 'bool', isRequired: false, isSecret: false, isComputed: false },
  { name: 'port', label: 'Port', kind: 'int', isRequired: false, isSecret: false, isComputed: false },
  {
    name: 'mode',
    label: 'Account type',
    kind: 'choice',
    isRequired: false,
    isSecret: false,
    isComputed: false,
    choices: ['apiKey', 'subscription'],
    defaultValue: 'apiKey',
  },
  { name: 'hosts', label: 'Hosts', kind: 'stringList', isRequired: false, isSecret: false, isComputed: false },
  { name: 'redirectUri', label: 'Redirect URI', kind: 'url', isRequired: false, isSecret: false, isComputed: true },
  {
    name: 'tier',
    label: 'Service tier',
    kind: 'string',
    isRequired: true,
    isSecret: false,
    isComputed: false,
    defaultValue: 'standard',
  },
]

const profile = {
  id: 'p1',
  displayName: 'Unknown family',
  providerKind: 'openAiCompatible',
  baseUrl: 'https://api.example.com/v1',
  authMode: 'openAiCompatible:ApiKey',
  isActive: false,
  configuredModels: [],
  purposeBindings: [],
  verification: { status: 'verified' },
  availability: { state: 'available', unresolvedValues: [] },
  providerSettings: { endpoint: 'https://api.example.com/v1', port: '1455' },
  declaredSecretNames: ['clientSecret'],
  declaredFields,
  computedFields: [
    { name: 'redirectUri', value: 'http://127.0.0.1:1455/auth/callback', refusal: 'Redirect URI must use https.' },
  ],
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

async function openTheEditor() {
  const wrapper = mountTab()
  await flushPromises()
  await wrapper.findAll('.ai-profile-card')[0].trigger('click')
  await flushPromises()
  return wrapper
}

describe('the connection form for a family the console has never seen', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    connections.listAiConnections.mockResolvedValue([profile])
    connections.listPermittedProviders.mockResolvedValue({
      providers: [
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [{ name: 'apiKey', label: 'API key', isSecret: true, isRequired: true }] },
          declaredFields,
        },
      ],
      isRestricted: false,
    })
  })

  // Every shape the vocabulary carries gets an input an operator can use, without a line of code per family.
  it('renders an input for each declared shape', async () => {
    const wrapper = await openTheEditor()

    expect(wrapper.find('[data-testid="ai-declared-endpoint-input"]').attributes('type')).toBe('url')
    expect(wrapper.find('[data-testid="ai-declared-region-input"]').attributes('type')).toBe('text')
    expect(wrapper.find('[data-testid="ai-declared-clientSecret-input"]').attributes('type')).toBe('password')
    expect(wrapper.find('[data-testid="ai-declared-useCache-input"]').attributes('type')).toBe('checkbox')
    expect(wrapper.find('[data-testid="ai-declared-port-input"]').attributes('type')).toBe('number')
    expect(wrapper.find('[data-testid="ai-declared-mode-input"]').element.tagName).toBe('SELECT')
    expect(wrapper.find('[data-testid="ai-declared-hosts-input"]').element.tagName).toBe('TEXTAREA')
  })

  it('offers each option a choice field declares', async () => {
    const wrapper = await openTheEditor()

    const options = wrapper.findAll('[data-testid="ai-declared-mode-input"] option')
    expect(options.map((option) => option.text())).toEqual(['apiKey', 'subscription'])
  })

  // A computed value is shown read-only, with the reason the installation would refuse it, and the rest of the
  // form is still usable.
  it('shows a computed value read-only, with the reason it would be refused', async () => {
    const wrapper = await openTheEditor()

    expect(wrapper.find('[data-testid="ai-declared-redirectUri-input"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="ai-declared-redirectUri-value"]').text()).toBe(
      'http://127.0.0.1:1455/auth/callback',
    )
    expect(wrapper.find('[data-testid="ai-declared-redirectUri-refusal"]').text()).toContain('must use https')
    expect(wrapper.find('[data-testid="ai-declared-endpoint-input"]').exists()).toBe(true)
  })

  // A stored secret is never returned, so the box is empty and the form says a value is set instead of looking
  // unconfigured.
  it('reports a stored secret as set and never renders its value', async () => {
    const wrapper = await openTheEditor()

    const input = wrapper.find('[data-testid="ai-declared-clientSecret-input"]')
    expect((input.element as HTMLInputElement).value).toBe('')
    expect(wrapper.find('[data-testid="ai-declared-clientSecret-set"]').text()).toContain('stored')
  })

  it('loads the stored values into the inputs', async () => {
    const wrapper = await openTheEditor()

    expect((wrapper.find('[data-testid="ai-declared-endpoint-input"]').element as HTMLInputElement).value)
      .toBe('https://api.example.com/v1')
    expect((wrapper.find('[data-testid="ai-declared-port-input"]').element as HTMLInputElement).value).toBe('1455')
  })

  // The family's default supplies the value when the box is left alone, and the server saves it on that basis.
  // Refusing here tells the operator to fill in a value that is already going to be sent.
  it('saves a required field the family declared a default for without asking the operator to retype it', async () => {
    // A model, because the save also refuses a profile with none and that refusal would mask this one.
    const withAModel = {
      ...profile,
      configuredModels: [{ id: 'm1', remoteModelId: 'gpt-4o', displayName: 'GPT-4o', operationKinds: ['chat'] }],
    } as unknown as AiConnectionDto
    connections.listAiConnections.mockResolvedValue([withAModel])
    connections.updateAiConnection.mockResolvedValue(withAModel)

    const wrapper = await openTheEditor()

    expect((wrapper.find('[data-testid="ai-declared-tier-input"]').element as HTMLInputElement).value).toBe('')

    await wrapper.find('[data-testid="ai-save-profile"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="ai-declared-tier-error"]').exists()).toBe(false)
    expect(connections.updateAiConnection).toHaveBeenCalled()
  })
})
