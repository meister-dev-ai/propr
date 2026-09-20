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
  dispatchProviderAction: vi.fn(),
  submitProviderActionValues: vi.fn(),
  readProviderActionInvocation: vi.fn(),
  ApiFieldValidationError: class extends Error {},
}))

vi.mock('@/services/aiConnectionsService', () => connections)
vi.mock('@/services/logicalModelsService', () => ({ listEffectiveForClient: vi.fn().mockResolvedValue([]) }))
vi.mock('@/services/modelCatalogService', () => ({
  listProviders: vi.fn().mockResolvedValue([]),
  listModels: vi.fn().mockResolvedValue([]),
}))

// Two operations declared by a family this console has no knowledge of, each with its own label.
const declaredActions = [
  {
    addInKey: 'meisterdev/example',
    id: 'connect',
    label: 'Connect account',
    inputs: [
      {
        name: 'callbackUrl',
        label: 'Callback URL',
        kind: 'string',
        isRequired: false,
        isSecret: false,
        isComputed: false,
      },
    ],
    coLocationNotice: 'This finishes on port 1455 on the machine running the host.',
  },
  {
    addInKey: 'meisterdev/example',
    id: 'disconnect',
    label: 'Disconnect account',
    inputs: [],
  },
]

const profile = {
  id: 'p1',
  displayName: 'Unknown family',
  providerKind: 'openAiCompatible',
  baseUrl: 'https://api.example.com/v1',
  authMode: 'apiKey',
  isActive: false,
  configuredModels: [],
  purposeBindings: [],
  verification: { status: 'verified' },
  availability: { state: 'available', unresolvedValues: [] },
  declaredFields: [],
  computedFields: [],
  declaredActions,
  offeredActionIds: ['connect', 'disconnect'],
} as unknown as AiConnectionDto

// The same family and the same declaration, on a connection its family says is offered only the sign-in.
const beforeConnecting = { ...profile, offeredActionIds: ['connect'] } as unknown as AiConnectionDto

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

describe('the operations a provider family declares, on the connection they apply to', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    connections.listAiConnections.mockResolvedValue([profile])
    connections.listPermittedProviders.mockResolvedValue({ providers: [], isRestricted: false })
  })

  // No affordance is written for any family: what is rendered is what the server said the family declares.
  it('renders one affordance per declared action, labelled as the family labelled it', async () => {
    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.find('[data-testid="ai-action-connect"]').text()).toBe('Connect account')
    expect(wrapper.find('[data-testid="ai-action-disconnect"]').text()).toBe('Disconnect account')
  })

  // The declaration is the same on both connections; what differs is what the family said this one is in a state
  // for. The affordance for the operation it is not offered is not rendered at all.
  it('renders only the declared actions the connection is offered', async () => {
    connections.listAiConnections.mockResolvedValue([beforeConnecting])

    const wrapper = mountTab()
    await flushPromises()

    expect(wrapper.find('[data-testid="ai-action-connect"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="ai-action-disconnect"]').exists()).toBe(false)
  })

  it('states what the deployment has to satisfy before the action starts', async () => {
    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-connect"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="ai-action-notice"]').text()).toBe(
      'This finishes on port 1455 on the machine running the host.',
    )
    expect(wrapper.find('[data-testid="ai-action-start"]').exists()).toBe(true)
    expect(connections.dispatchProviderAction).not.toHaveBeenCalled()
  })

  it('shows what a completed run said', async () => {
    connections.dispatchProviderAction.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'disconnect',
        state: 'Completed',
        message: 'The account was disconnected.',
        expiresAt: new Date().toISOString(),
      },
      result: { kind: 'completed', message: 'The account was disconnected.' },
    })

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-disconnect"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="ai-action-completed"]').text()).toBe('The account was disconnected.')
  })

  it('renders the fields a form result asked for and submits them against the same run', async () => {
    connections.dispatchProviderAction.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Pending',
        waitingFor: 'Waiting for the values.',
        expiresAt: new Date(Date.now() + 600_000).toISOString(),
      },
      result: {
        kind: 'showForm',
        fields: [
          {
            name: 'callbackUrl',
            label: 'Callback URL',
            kind: 'string',
            isRequired: true,
            isSecret: false,
            isComputed: false,
          },
        ],
      },
    })
    connections.submitProviderActionValues.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Completed',
        message: 'Connected.',
        expiresAt: new Date().toISOString(),
      },
      result: { kind: 'completed', message: 'Connected.' },
    })

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-connect"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-testid="ai-action-start"]').trigger('click')
    await flushPromises()

    const input = wrapper.find('[data-testid="ai-action-field-callbackUrl-input"]')
    expect(input.exists()).toBe(true)
    await input.setValue('https://auth.example.com/cb?code=abc')
    await wrapper.find('[data-testid="ai-action-submit"]').trigger('click')
    await flushPromises()

    expect(connections.submitProviderActionValues).toHaveBeenCalledWith('run-1', {
      callbackUrl: 'https://auth.example.com/cb?code=abc',
    })
    expect(wrapper.find('[data-testid="ai-action-completed"]').text()).toBe('Connected.')
  })

  // A computed input is derived by the family, and the connection form leaves those out of what it saves. The
  // action path submitting one sends the family a value for a field it said it would compute.
  it('submits no value for an input the family declared it computes', async () => {
    connections.dispatchProviderAction.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Pending',
        waitingFor: 'Waiting for the values.',
        expiresAt: new Date(Date.now() + 600_000).toISOString(),
      },
      result: {
        kind: 'showForm',
        fields: [
          {
            name: 'callbackUrl',
            label: 'Callback URL',
            kind: 'string',
            isRequired: true,
            isSecret: false,
            isComputed: false,
          },
          {
            name: 'redirectUri',
            label: 'Redirect address',
            kind: 'string',
            isRequired: false,
            isSecret: false,
            isComputed: true,
            defaultValue: 'http://127.0.0.1:1455/callback',
          },
        ],
      },
    })
    connections.submitProviderActionValues.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Completed',
        message: 'Connected.',
        expiresAt: new Date().toISOString(),
      },
      result: { kind: 'completed', message: 'Connected.' },
    })

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-connect"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-testid="ai-action-start"]').trigger('click')
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-field-callbackUrl-input"]').setValue('https://auth.example.com/cb')
    await wrapper.find('[data-testid="ai-action-submit"]').trigger('click')
    await flushPromises()

    expect(connections.submitProviderActionValues).toHaveBeenCalledWith('run-1', {
      callbackUrl: 'https://auth.example.com/cb',
    })
  })

  // The connection form renders a choice as a select and a boolean as a checkbox. An action's inputs come from
  // the same declared vocabulary, so a text box for either takes a value the family declared it would not get.
  it('renders an action input of every declared shape the way the connection form renders it', async () => {
    connections.dispatchProviderAction.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Pending',
        waitingFor: 'Waiting for the values.',
        expiresAt: new Date(Date.now() + 600_000).toISOString(),
      },
      result: {
        kind: 'showForm',
        fields: [
          {
            name: 'tier',
            label: 'Tier',
            kind: 'choice',
            isRequired: false,
            isSecret: false,
            isComputed: false,
            choices: ['standard', 'premium'],
          },
          { name: 'useCache', label: 'Use cache', kind: 'bool', isRequired: false, isSecret: false, isComputed: false },
        ],
      },
    })

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-connect"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-testid="ai-action-start"]').trigger('click')
    await flushPromises()

    const tier = wrapper.find('[data-testid="ai-action-field-tier-input"]')
    expect(tier.element.tagName).toBe('SELECT')
    expect(tier.findAll('option').map((option) => option.text())).toEqual(['standard', 'premium'])

    const useCache = wrapper.find('[data-testid="ai-action-field-useCache-input"]')
    expect(useCache.attributes('type')).toBe('checkbox')
  })

  // A required input left blank is refused where the message can name the field, rather than at the family,
  // which answers with a refusal of its own wording against an invocation the operator has to go and read.
  it('refuses to submit an action form with a required input left blank', async () => {
    connections.dispatchProviderAction.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Pending',
        waitingFor: 'Waiting for the values.',
        expiresAt: new Date(Date.now() + 600_000).toISOString(),
      },
      result: {
        kind: 'showForm',
        fields: [
          {
            name: 'callbackUrl',
            label: 'Callback URL',
            kind: 'string',
            isRequired: true,
            isSecret: false,
            isComputed: false,
          },
        ],
      },
    })

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-connect"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-testid="ai-action-start"]').trigger('click')
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-submit"]').trigger('click')
    await flushPromises()

    expect(connections.submitProviderActionValues).not.toHaveBeenCalled()
    expect(wrapper.find('[data-testid="ai-action-error"]').text()).toContain('Callback URL is required.')
  })

  it('shows what an open run is waiting for', async () => {
    vi.spyOn(window, 'open').mockReturnValue(null)
    connections.dispatchProviderAction.mockResolvedValue({
      invocation: {
        id: 'run-1',
        connectionId: 'p1',
        addInKey: 'meisterdev/example',
        actionId: 'connect',
        state: 'Pending',
        waitingFor: 'Waiting for the provider to reach port 1455 on this machine.',
        expiresAt: new Date(Date.now() + 600_000).toISOString(),
      },
      result: { kind: 'openUrl', url: 'https://auth.example.com/authorize', awaitCompletion: true },
    })
    connections.readProviderActionInvocation.mockResolvedValue({
      id: 'run-1',
      connectionId: 'p1',
      addInKey: 'meisterdev/example',
      actionId: 'connect',
      state: 'Pending',
      waitingFor: 'Waiting for the provider to reach port 1455 on this machine.',
      expiresAt: new Date(Date.now() + 600_000).toISOString(),
    })

    const wrapper = mountTab()
    await flushPromises()

    await wrapper.find('[data-testid="ai-action-connect"]').trigger('click')
    await flushPromises()
    await wrapper.find('[data-testid="ai-action-start"]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-testid="ai-action-pending"]').text()).toContain('1455')
    expect(wrapper.find('[data-testid="ai-action-opened"]').exists()).toBe(true)

    wrapper.unmount()
  })
})
