// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h, nextTick } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  ApiFieldValidationError,
  createAiConnection,
  listAiConnections,
  listPermittedProviders,
} from '@/services/aiConnectionsService'
import type { AiConnectionDto, AiDeclaredFieldDto } from '@/services/aiConnectionsService'
import { useClientAiConnectionsTab } from '../useClientAiConnectionsTab'

vi.mock('@/services/aiConnectionsService', async () => {
  const actual = await vi.importActual<typeof import('@/services/aiConnectionsService')>(
    '@/services/aiConnectionsService',
  )

  return {
    ApiFieldValidationError: actual.ApiFieldValidationError,
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
  }
})

const apiKeyField = { name: 'apiKey', label: 'API key', isSecret: true, isRequired: true }

// A family this frontend has never seen, described only by what the server says it declares.
const declaredFields: AiDeclaredFieldDto[] = [
  {
    name: 'endpoint',
    label: 'Endpoint',
    kind: 'url',
    isRequired: true,
    isSecret: false,
    isComputed: false,
    placeholder: 'https://api.example.com/v1',
  },
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
  {
    name: 'clientSecret',
    label: 'Client secret',
    kind: 'secret',
    isRequired: false,
    isSecret: true,
    isComputed: false,
    visibleWhen: { fieldName: 'mode', equalsValue: 'subscription' },
  },
  {
    name: 'redirectUri',
    label: 'Redirect URI',
    kind: 'url',
    isRequired: false,
    isSecret: false,
    isComputed: true,
  },
  { name: 'port', label: 'Port', kind: 'int', isRequired: false, isSecret: false, isComputed: false },
  { name: 'useCache', label: 'Use cache', kind: 'bool', isRequired: false, isSecret: false, isComputed: false },
  { name: 'hosts', label: 'Hosts', kind: 'stringList', isRequired: false, isSecret: false, isComputed: false },
]

let api!: ReturnType<typeof useClientAiConnectionsTab>

async function mountComposable() {
  mount(
    defineComponent({
      setup() {
        api = useClientAiConnectionsTab({ clientId: 'c1' })
        return () => h('div')
      },
    }),
  )
  await flushPromises()
}

const withOneModel = () => {
  api.editor.displayName = 'Declared'
  api.editor.baseUrl = 'https://api.example.com/v1'
  api.editor.credentials.apiKey = 'sk-test'
  api.addModel()
  api.editor.models[0].remoteModelId = 'gpt-4o'
}

describe('the form a provider family declares', () => {
  beforeEach(async () => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(createAiConnection).mockReset()
    vi.mocked(listPermittedProviders).mockResolvedValue({
      isRestricted: false,
      providers: [
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }, { value: 'openAiCompatible:ChatCompletions', label: 'Chat Completions' }],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [apiKeyField] },
          declaredFields,
        },
      ],
    })

    await mountComposable()
    api.openCreateEditor()
    api.editor.providerKind = 'openAiCompatible'
    await nextTick()
  })

  // A family the frontend has never seen still gets a complete form, because every field on it came from the
  // server rather than from a table compiled into this build.
  it('renders every field the family declares except the ones a condition hides', () => {
    expect(api.visibleDeclaredFields.value.map((field) => field.name)).toEqual([
      'endpoint',
      'mode',
      'redirectUri',
      'port',
      'useCache',
      'hosts',
    ])
  })

  it('shows and hides a dependent field as the field it depends on changes', async () => {
    expect(api.visibleDeclaredFields.value.some((field) => field.name === 'clientSecret')).toBe(false)

    api.editor.declaredValues.mode = 'subscription'
    await nextTick()

    expect(api.visibleDeclaredFields.value.some((field) => field.name === 'clientSecret')).toBe(true)
  })

  it('submits every value the form shows, and neither the hidden nor the computed one', async () => {
    vi.mocked(createAiConnection).mockResolvedValue({ id: 'p1' } as AiConnectionDto)
    withOneModel()
    api.editor.declaredValues.endpoint = 'https://api.example.com/v1'
    api.editor.declaredValues.port = '1455'
    api.editor.declaredValues.useCache = 'true'
    api.editor.declaredValues.hosts = 'a\nb'
    api.editor.declaredValues.redirectUri = 'https://tampered.example.com/auth'

    await api.saveProfile()

    const request = vi.mocked(createAiConnection).mock.calls[0][1]
    expect(request.providerSettings).toEqual({
      endpoint: 'https://api.example.com/v1',
      mode: 'apiKey',
      port: '1455',
      useCache: 'true',
      hosts: 'a\nb',
    })
  })

  it('blocks a save when a required field is empty, and says which field', async () => {
    withOneModel()

    await api.saveProfile()

    expect(createAiConnection).not.toHaveBeenCalled()
    expect(api.saveError.value).toContain('Endpoint is required')
    expect(api.declaredFieldError('endpoint')).toContain('Endpoint is required')
  })

  it('attaches a message the server keyed to a field to that field', async () => {
    vi.mocked(createAiConnection).mockRejectedValue(
      new ApiFieldValidationError('One or more validation errors occurred.', {
        'providerSettings.endpoint': ['Endpoint must use https.'],
      }),
    )
    withOneModel()
    api.editor.declaredValues.endpoint = 'http://api.example.com/v1'

    await api.saveProfile()

    expect(api.declaredFieldError('endpoint')).toBe('Endpoint must use https.')
  })

  // A hidden field produces no message, so an operator cannot be blocked by a refusal about a field the form
  // does not show.
  it('does not require a hidden field', async () => {
    vi.mocked(createAiConnection).mockResolvedValue({ id: 'p1' } as AiConnectionDto)
    const requiredWhenVisible: AiDeclaredFieldDto[] = declaredFields.map((field) =>
      field.name === 'clientSecret' ? { ...field, isRequired: true } : field,
    )

    vi.mocked(listPermittedProviders).mockResolvedValue({
      isRestricted: false,
      providers: [
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }, { value: 'openAiCompatible:ChatCompletions', label: 'Chat Completions' }],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [apiKeyField] },
          declaredFields: requiredWhenVisible,
        },
      ],
    })

    await api.refreshProfiles()
    api.openCreateEditor()
    api.editor.providerKind = 'openAiCompatible'
    withOneModel()
    api.editor.declaredValues.endpoint = 'https://api.example.com/v1'
    await nextTick()

    await api.saveProfile()

    expect(api.declaredFieldError('clientSecret')).toBe('')
    expect(createAiConnection).toHaveBeenCalled()
  })
})

describe('a stored connection of a declared family', () => {
  const stored: AiConnectionDto = {
    id: 'p1',
    displayName: 'Declared',
    providerKind: 'openAiCompatible',
    baseUrl: 'https://api.example.com/v1',
    authMode: 'openAiCompatible:ApiKey',
    discoveryMode: 'manualOnly',
    providerSettings: { endpoint: 'https://api.example.com/v1', port: '1455' },
    declaredSecretNames: ['clientSecret'],
    declaredFields,
    computedFields: [
      { name: 'redirectUri', value: 'http://127.0.0.1:1455/auth/callback', refusal: 'Redirect URI must use https.' },
    ],
  } as AiConnectionDto

  beforeEach(async () => {
    vi.mocked(listAiConnections).mockResolvedValue([stored])
    vi.mocked(listPermittedProviders).mockResolvedValue({
      isRestricted: false,
      providers: [
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }, { value: 'openAiCompatible:ChatCompletions', label: 'Chat Completions' }],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [apiKeyField] },
          declaredFields,
        },
      ],
    })

    await mountComposable()
    api.openEditEditor(stored)
    await nextTick()
  })

  it('loads the stored values into the form', () => {
    expect(api.editor.declaredValues.endpoint).toBe('https://api.example.com/v1')
    expect(api.editor.declaredValues.port).toBe('1455')
  })

  // A stored secret is never returned, so the form says a value is set rather than showing an empty box that
  // reads as unconfigured.
  it('reports a stored secret as set without holding its value', () => {
    expect(api.storedDeclaredSecretNames.value).toEqual(['clientSecret'])
    expect(api.editor.declaredValues.clientSecret).toBeUndefined()
  })

  it('shows the computed value the server returned, with the reason it would be refused', () => {
    expect(api.computedDeclaredValues.value.redirectUri.value).toBe('http://127.0.0.1:1455/auth/callback')
    expect(api.computedDeclaredValues.value.redirectUri.refusal).toBe('Redirect URI must use https.')
  })
})
