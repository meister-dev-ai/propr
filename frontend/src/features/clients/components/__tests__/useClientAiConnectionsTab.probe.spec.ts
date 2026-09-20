// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { listAiConnections, listPermittedProviders, probeAiConnection } from '@/services/aiConnectionsService'
import { useClientAiConnectionsTab } from '../useClientAiConnectionsTab'

// What each family's driver declares its credential is made of, as the permitted-providers endpoint reports it.
// The form renders these, so a fixture that omitted them would describe a family whose credential cannot be
// entered at all.
const apiKeyField = { name: 'apiKey', label: 'API key', isSecret: true, isRequired: true }
const accessKeyIdField = { name: 'accessKeyId', label: 'Access key ID', isSecret: false, isRequired: true }
const secretAccessKeyField = { name: 'secretAccessKey', label: 'Secret access key', isSecret: true, isRequired: true }

vi.mock('@/services/aiConnectionsService', () => ({
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

describe('probing a connection before saving it', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'azureOpenAi:Responses', label: 'Responses' },
            { value: 'azureOpenAi:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }, { value: 'azureOpenAi:AzureIdentity', label: 'Azure Identity' }],
          credentialFields: { 'azureOpenAi:ApiKey': [apiKeyField], 'azureOpenAi:AzureIdentity': [] },
        },
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'openAiCompatible:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [apiKeyField] },
        },
      ],
      isRestricted: false,
    })
    vi.mocked(probeAiConnection).mockReset()
  })

  it('reports success without saving anything', async () => {
    await mountComposable()
    vi.mocked(probeAiConnection).mockResolvedValue({ status: 'verified', summary: 'Reached 12 models.' } as never)

    await api.handleProbeConnection()

    expect(api.probeFailed.value).toBe(false)
    expect(api.probeMessage.value).toBe('Reached 12 models.')
  })

  // A refused probe is the whole reason the button exists, so the provider's own reason and its hint both have to
  // reach the operator — a bad key and an unreachable host need different fixes.
  it('shows the provider reason and what to try next when refused', async () => {
    await mountComposable()
    vi.mocked(probeAiConnection).mockResolvedValue({
      status: 'failed',
      summary: 'The provider rejected the credential (HTTP 401).',
      actionHint: 'Check the configured API key or credential source.',
    } as never)

    await api.handleProbeConnection()

    expect(api.probeFailed.value).toBe(true)
    expect(api.probeMessage.value).toContain('HTTP 401')
    expect(api.probeMessage.value).toContain('API key')
  })

  it('reports a refusal from the server rather than failing silently', async () => {
    await mountComposable()
    vi.mocked(probeAiConnection).mockRejectedValue(
      new Error('This profile cannot be probed because the provider is not permitted.'),
    )

    await api.handleProbeConnection()

    expect(api.probeFailed.value).toBe(true)
    expect(api.probeMessage.value).toContain('not permitted')
  })
})

// The probe sends the credential in its request, so it can only ever test a key present in the form. A saved
// profile's key is never returned to the browser, which used to leave the button offering a test that came back
// as a validation refusal about a missing key rather than as a missing input.
describe('offering the probe only when there is something to probe with', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }, { value: 'azureOpenAi:Responses', label: 'Responses' }],
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }, { value: 'azureOpenAi:AzureIdentity', label: 'Azure Identity' }],
          credentialFields: { 'azureOpenAi:ApiKey': [apiKeyField], 'azureOpenAi:AzureIdentity': [] },
        },
      ],
      isRestricted: false,
    })
    vi.mocked(probeAiConnection).mockReset()
  })

  it('is unavailable while a required credential field is empty', async () => {
    await mountComposable()
    api.editor.authMode = 'azureOpenAi:ApiKey'
    api.editor.credentials = {}

    expect(api.canProbe.value).toBe(false)
  })

  it('becomes available once every required field is filled', async () => {
    await mountComposable()
    api.editor.authMode = 'azureOpenAi:ApiKey'
    api.editor.credentials = { apiKey: 'sk-test' }

    expect(api.canProbe.value).toBe(true)
  })

  it('stays available for azure identity, which carries no credential at all', async () => {
    await mountComposable()
    api.editor.authMode = 'azureOpenAi:AzureIdentity'
    api.editor.credentials = {}

    expect(api.canProbe.value).toBe(true)
  })

  // A result belongs to the credential that produced it, so opening another profile must not inherit it.
  it('drops a previous result when a different profile is opened', async () => {
    await mountComposable()
    vi.mocked(probeAiConnection).mockResolvedValue({
      status: 'failed',
      summary: 'The provider rejected the credential (HTTP 401).',
    } as never)

    await api.handleProbeConnection()
    expect(api.probeMessage.value).toContain('HTTP 401')

    api.openEditEditor({
      id: 'other-profile',
      displayName: 'Another provider',
      providerKind: 'azureOpenAi',
      declaredFields: [],
      baseUrl: 'https://other.openai.azure.com/',
      authMode: 'azureOpenAi:ApiKey',
    } as never)

    expect(api.probeMessage.value).toBe('')
    expect(api.probeFailed.value).toBe(false)
  })

  it('drops a previous result when the form is reset for a new profile', async () => {
    await mountComposable()
    vi.mocked(probeAiConnection).mockResolvedValue({ status: 'failed', summary: 'Refused.' } as never)

    await api.handleProbeConnection()
    expect(api.probeMessage.value).toBe('Refused.')

    api.openCreateEditor()

    expect(api.probeMessage.value).toBe('')
    expect(api.probeFailed.value).toBe(false)
  })
})
