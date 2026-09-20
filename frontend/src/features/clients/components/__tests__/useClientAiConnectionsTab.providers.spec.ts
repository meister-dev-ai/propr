// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h, nextTick } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { listAiConnections, listPermittedProviders, probeAiConnection, updateAiConnection } from '@/services/aiConnectionsService'
import type { AiConnectionDto, AiProviderKind } from '@/services/aiConnectionsService'
import { useClientAiConnectionsTab } from '../useClientAiConnectionsTab'

// What each family's driver declares its credential is made of, as the permitted-providers endpoint reports it.
// The form renders these, so a fixture that omitted them would describe a family whose credential cannot be
// entered at all.
const apiKeyField = { name: 'apiKey', label: 'API key', isSecret: true, isRequired: true }

// Authentication modes as they persist and as the server reports them: the declaring family's key joined to the
// mode name.
const azureApiKey = 'azureOpenAi:ApiKey'
const azureIdentity = 'azureOpenAi:AzureIdentity'
const bedrockApiKey = 'awsBedrock:ApiKey'
const bedrockSigV4 = 'awsBedrock:SigV4'
const compatibleApiKey = 'openAiCompatible:ApiKey'
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

// A stored profile carrying nothing but the family it names, the subject of the availability question.
const profileOn = (providerKind: AiProviderKind): AiConnectionDto =>
  ({ id: 'p1', displayName: 'Profile', providerKind }) as AiConnectionDto

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

describe('which provider families the form offers', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(listPermittedProviders).mockReset()
  })

  // The provider enum names families this build has no driver for. The server decides what is offerable, so a
  // named-but-unimplemented family must never reach the picker — otherwise opening the enum moves the failure to
  // review time, which the offer exists to avoid.
  it('offers only what the server says this client can configure', async () => {
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
          providerKind: 'openAi',
          label: 'OpenAI (non-Azure)',
          declaredFields: [],
          isPermitted: false,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'openAi:Responses', label: 'Responses' },
            { value: 'openAi:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'openAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAi:ApiKey': [apiKeyField] },
        },
        {
          providerKind: 'liteLlm',
          label: 'LiteLLM',
          declaredFields: [],
          isPermitted: false,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'liteLlm:Responses', label: 'Responses' },
            { value: 'liteLlm:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'liteLlm:ApiKey', label: 'API Key' }],
          credentialFields: { 'liteLlm:ApiKey': [apiKeyField] },
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

    await mountComposable()

    const offered = api.availableProviderOptions.value.map((option) => option.value)
    expect(offered).toEqual(['azureOpenAi', 'openAiCompatible'])
    expect(offered).not.toContain('anthropic')
  })

  // The two reasons need different fixes: one is the tenant's policy, the other is this build. Reporting them
  // apart is the difference between an operator editing a policy and an operator waiting for a release.
  it('tells a tenant refusal apart from a missing driver', async () => {
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
          providerKind: 'openAi',
          label: 'OpenAI (non-Azure)',
          declaredFields: [],
          isPermitted: false,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'openAi:Responses', label: 'Responses' },
            { value: 'openAi:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'openAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAi:ApiKey': [apiKeyField] },
        },
        {
          providerKind: 'liteLlm',
          label: 'LiteLLM',
          declaredFields: [],
          isPermitted: false,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'liteLlm:Responses', label: 'Responses' },
            { value: 'liteLlm:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'liteLlm:ApiKey', label: 'API Key' }],
          credentialFields: { 'liteLlm:ApiKey': [apiKeyField] },
        },
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          declaredFields: [],
          isPermitted: false,
          protocolModes: [
            { value: 'Auto', label: 'Auto' },
            { value: 'openAiCompatible:ChatCompletions', label: 'Chat Completions' },
            { value: 'Embeddings', label: 'Embeddings' },
          ],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [apiKeyField] },
        },
      ],
      isRestricted: true,
    })

    await mountComposable()

    expect(api.connectionAvailability(profileOn('liteLlm'))?.reason).toBe('providerFamilyNotPermitted')
    expect(api.connectionAvailability(profileOn('anthropic'))?.reason).toBe('providerFamilyAbsent')
    expect(api.connectionAvailability(profileOn('azureOpenAi'))).toBeUndefined()
  })

  // A profile the server itself marked unusable is reported as the server described it, whatever family the
  // enum-typed field on it happens to name.
  it('reports what the profile says about itself before asking about its family', async () => {
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
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'azureOpenAi:ApiKey': [apiKeyField] },
        },
      ],
      isRestricted: false,
    })

    await mountComposable()

    const quarantined = {
      ...profileOn('azureOpenAi'),
      availability: {
        state: 'unavailable' as const,
        reason: 'providerFamilyAbsent' as const,
        providerIdentity: 'ContosoLlm',
        unresolvedValues: [],
      },
    }

    expect(api.connectionAvailability(quarantined)?.providerIdentity).toBe('ContosoLlm')
  })

  // The console names no family of its own, so a lookup that failed leaves nothing to offer. What it must not
  // do is start reporting stored profiles as refused: nothing was learned about the policy, so nothing is
  // claimed about it, and the server still refuses anything invalid.
  it('offers nothing, and refuses nothing, when the lookup fails', async () => {
    vi.mocked(listPermittedProviders).mockRejectedValue(new Error('nope'))

    await mountComposable()

    expect(api.availableProviderOptions.value).toEqual([])
    expect(api.isProviderPermitted('azureOpenAi')).toBe(true)
    expect(api.connectionAvailability(profileOn('azureOpenAi'))).toBeUndefined()
  })

  // A profile stored against a family this build no longer has still opens on that family, named by the key it
  // is stored under, so editing it cannot silently move it to another family.
  it('keeps the family a profile is stored against in the picker when the server does not describe it', async () => {
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }],
          credentialFields: { 'azureOpenAi:ApiKey': [apiKeyField] },
        },
      ],
      isRestricted: false,
    })

    await mountComposable()

    api.editor.providerKind = 'googleVertex'
    await nextTick()

    expect(api.availableProviderOptions.value).toContainEqual({ value: 'googleVertex', label: 'googleVertex' })
    expect(api.providerLabel('googleVertex')).toBe('googleVertex')
  })
})

describe('which authentication modes the form offers', () => {
  beforeEach(() => {
    // Reset every mock these tests read back, or an assertion on the first recorded call reads the previous
    // test's call and passes whatever this test did.
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(probeAiConnection).mockReset()
    vi.mocked(updateAiConnection).mockReset()
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'azureOpenAi:ApiKey', label: 'API Key' }, { value: 'azureOpenAi:AzureIdentity', label: 'Azure Identity' }],
          credentialFields: { 'azureOpenAi:ApiKey': [apiKeyField], 'azureOpenAi:AzureIdentity': [] },
        },
        {
          providerKind: 'awsBedrock',
          label: 'AWS Bedrock',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'awsBedrock:ApiKey', label: 'API Key' }, { value: 'awsBedrock:SigV4', label: 'AWS Signature v4' }],
          credentialFields: { 'awsBedrock:ApiKey': [apiKeyField], 'awsBedrock:SigV4': [accessKeyIdField, secretAccessKeyField] },
        },
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'openAiCompatible:ApiKey', label: 'API Key' }],
          credentialFields: { 'openAiCompatible:ApiKey': [apiKeyField] },
        },
      ],
      isRestricted: false,
    })
  })

  // Only the family's driver knows which authentication modes it can read, and they differ: an Azure resource takes
  // a managed identity, Bedrock signs with an access key, an arbitrary compatible server reads a bearer key.
  it('offers the selected family the shapes the server reported for it', async () => {
    await mountComposable()

    expect(api.authModeOptions.value.map((option) => option.value)).toEqual([azureApiKey, azureIdentity])

    api.editor.providerKind = 'awsBedrock'
    await nextTick()

    expect(api.authModeOptions.value.map((option) => option.value)).toEqual([bedrockApiKey, bedrockSigV4])
  })

  // Switching family can strand the form on a mode the new family cannot read, which the server then refuses on
  // save. The selection moves to one the new family declares instead — for every mode, not only the Azure one.
  it('moves off a mode the newly selected family does not declare', async () => {
    await mountComposable()

    api.editor.providerKind = 'awsBedrock'
    await nextTick()
    api.editor.authMode = bedrockSigV4

    api.editor.providerKind = 'openAiCompatible'
    await nextTick()
    api.handleProviderKindChange()

    expect(api.editor.authMode).toBe(compatibleApiKey)
  })

  // A mode the selected family does declare is left alone, so switching does not throw away a choice that is
  // still valid.
  it('keeps a mode the selected family declares', async () => {
    await mountComposable()

    api.editor.providerKind = 'awsBedrock'
    await nextTick()
    api.editor.authMode = bedrockSigV4
    api.handleProviderKindChange()

    expect(api.editor.authMode).toBe(bedrockSigV4)
  })

  // A credential is one key for most families and several values for some, so the form renders what the driver
  // declared for the selected mode. Holding its own answer would offer a key box for a credential that is three
  // fields, which is exactly the shape that could not be configured at all.
  it('renders the fields the selected family and mode declare', async () => {
    await mountComposable()

    api.editor.providerKind = 'awsBedrock'
    api.editor.authMode = bedrockSigV4
    await nextTick()

    expect(api.credentialFields.value.map((field) => field.name)).toEqual(['accessKeyId', 'secretAccessKey'])

    api.editor.authMode = bedrockApiKey
    await nextTick()

    expect(api.credentialFields.value.map((field) => field.name)).toEqual(['apiKey'])
  })

  // An ambient identity has nothing for an operator to enter, and a key box rendered for it would ask for the
  // credential the mode exists to avoid.
  it('renders nothing for a mode that stores no credential', async () => {
    await mountComposable()

    api.editor.authMode = azureIdentity
    await nextTick()

    expect(api.credentialFields.value).toEqual([])
  })

  // Only the fields the selected mode declares are submitted. A value typed under another mode stays in the
  // form and is not sent, because the family has nowhere to put it and the server refuses the name.
  it('submits only the fields the selected mode declares', async () => {
    await mountComposable()

    api.editor.providerKind = 'awsBedrock'
    api.editor.authMode = bedrockSigV4
    await nextTick()
    api.editor.credentials = { accessKeyId: ' AKIAEXAMPLE ', secretAccessKey: 'a-secret', apiKey: 'left-over' }

    vi.mocked(probeAiConnection).mockResolvedValue({ status: 'verified', summary: 'ok' } as never)
    await api.handleProbeConnection()

    expect(vi.mocked(probeAiConnection).mock.calls[0][1].auth).toEqual({
      mode: bedrockSigV4,
      fields: { accessKeyId: 'AKIAEXAMPLE', secretAccessKey: 'a-secret' },
    })
  })

  // On an edit the boxes start blank because a stored credential is never returned, so sending nothing is how
  // the form asks for the stored one to be kept.
  it('sends no credential when nothing was entered', async () => {
    await mountComposable()

    api.editor.credentials = { apiKey: '   ' }

    vi.mocked(probeAiConnection).mockResolvedValue({ status: 'verified', summary: 'ok' } as never)
    await api.handleProbeConnection()

    expect(vi.mocked(probeAiConnection).mock.calls[0][1].auth?.fields).toBeUndefined()
  })

  // A failed lookup leaves no per-family answer, and the console holds no catalogue of its own to fall back on.
  // The mode the open profile is stored under is kept so the form reads as what it holds.
  it('keeps the stored authentication mode when the lookup fails', async () => {
    vi.mocked(listPermittedProviders).mockRejectedValue(new Error('nope'))

    await mountComposable()

    api.editor.authMode = 'contoso/llm:GcpAdc'
    await nextTick()

    expect(api.authModeOptions.value).toEqual([{ value: 'contoso/llm:GcpAdc', label: 'contoso/llm:GcpAdc' }])
  })

  // A declaration that arrived and is empty is an answer. Treating it as a failed lookup would offer every mode
  // in the catalogue for a family that reads none of them.
  it('offers nothing for a family that declared no authentication mode', async () => {
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'azureOpenAi',
          label: 'Azure OpenAI / AI Foundry',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [],
          credentialFields: {},
        },
      ],
      isRestricted: false,
    })

    await mountComposable()

    expect(api.authModeOptions.value).toEqual([])
  })

  // Such a family has nothing for the selection to move to, so the mode chosen for the previous family stays in
  // the form. Sending it would ask the family to read a authentication mode it does not declare, so the save is
  // refused here and names the family.
  it('refuses to save against a family that declares no authentication mode', async () => {
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'awsBedrock',
          label: 'AWS Bedrock',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'awsBedrock:SigV4', label: 'AWS Signature v4' }],
          credentialFields: { 'awsBedrock:SigV4': [accessKeyIdField, secretAccessKeyField] },
        },
        {
          providerKind: 'openAiCompatible',
          label: 'OpenAI-compatible (custom base URL)',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [],
          credentialFields: {},
        },
      ],
      isRestricted: false,
    })

    await mountComposable()

    api.editor.providerKind = 'awsBedrock'
    await nextTick()
    api.editor.authMode = bedrockSigV4

    api.editor.providerKind = 'openAiCompatible'
    await nextTick()
    api.handleProviderKindChange()

    api.editor.displayName = 'Compatible'
    api.editor.baseUrl = 'https://llm.example.com/v1'
    api.addModel()
    api.editor.models[0].remoteModelId = 'some-model'

    await api.saveProfile()

    expect(api.saveError.value).toContain('no authentication mode')
    expect(vi.mocked(updateAiConnection)).not.toHaveBeenCalled()
  })

  // Only a change of provider kind moves the selection, so a stored profile opened for editing keeps its mode
  // whatever the family declares today. A family that narrowed its declaration would otherwise have a mode
  // submitted that it can no longer read.
  it('refuses to save a stored mode the family no longer declares, and names the alternatives', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([
      {
        id: 'p1',
        displayName: 'Bedrock',
        providerKind: 'awsBedrock',
        declaredFields: [],
        baseUrl: 'https://bedrock-runtime.eu-central-1.amazonaws.com',
        authMode: 'awsBedrock:SigV4',
        configuredModels: [{ id: 'm1', remoteModelId: 'anthropic.claude-3-5-sonnet' }],
      } as unknown as AiConnectionDto,
    ])
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'awsBedrock',
          label: 'AWS Bedrock',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'awsBedrock:ApiKey', label: 'API Key' }],
          credentialFields: { 'awsBedrock:ApiKey': [apiKeyField] },
        },
      ],
      isRestricted: false,
    })

    await mountComposable()

    api.openEditEditor(api.profiles.value[0])
    await nextTick()

    expect(api.editor.authMode).toBe(bedrockSigV4)

    await api.saveProfile()

    // The stored mode is named by the value it is stored under, because the family that named it no longer
    // declares it and nothing else can name it. The alternatives are named as the family names them.
    expect(api.saveError.value).toContain(bedrockSigV4)
    expect(api.saveError.value).toContain('API Key')
    expect(vi.mocked(updateAiConnection)).not.toHaveBeenCalled()
  })

  // The stored mode is still one the family declares, so nothing stands in the way of saving the edit. The
  // refusal above must not catch this.
  it('saves an edit whose stored mode the family still declares', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([
      {
        id: 'p1',
        displayName: 'Bedrock',
        providerKind: 'awsBedrock',
        declaredFields: [],
        baseUrl: 'https://bedrock-runtime.eu-central-1.amazonaws.com',
        authMode: 'awsBedrock:SigV4',
        configuredModels: [{ id: 'm1', remoteModelId: 'anthropic.claude-3-5-sonnet' }],
      } as unknown as AiConnectionDto,
    ])
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'awsBedrock',
          label: 'AWS Bedrock',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'awsBedrock:SigV4', label: 'AWS Signature v4' }],
          credentialFields: { 'awsBedrock:SigV4': [accessKeyIdField, secretAccessKeyField] },
        },
      ],
      isRestricted: false,
    })
    vi.mocked(updateAiConnection).mockResolvedValue({} as never)

    await mountComposable()

    api.openEditEditor(api.profiles.value[0])
    await nextTick()

    await api.saveProfile()

    expect(api.saveError.value).toBe('')
    expect(vi.mocked(updateAiConnection)).toHaveBeenCalled()
  })
})

describe('a authentication mode a family still reads but no longer offers', () => {
  // Both Bedrock shapes hold the same access-key pair, one packed into a single box and one as the parts AWS
  // names. The family still reads the packed one, so the server reports it, flagged.
  const bedrock = {
    providerKind: 'awsBedrock' as AiProviderKind,
    label: 'AWS Bedrock',
    declaredFields: [],
    isPermitted: true,
    protocolModes: [{ value: 'Auto', label: 'Auto' }],
    authModes: [
      { value: bedrockApiKey, label: 'Access key pair (earlier format)', isSuperseded: true },
      { value: bedrockSigV4, label: 'AWS Signature v4' },
    ],
    credentialFields: {
      [bedrockApiKey]: [apiKeyField],
      [bedrockSigV4]: [accessKeyIdField, secretAccessKeyField],
    },
  }

  const storedUnderThePackedPair = {
    id: 'p1',
    displayName: 'Bedrock',
    providerKind: 'awsBedrock',
    declaredFields: [],
    baseUrl: 'https://bedrock-runtime.eu-central-1.amazonaws.com',
    authMode: bedrockApiKey,
    configuredModels: [{ id: 'm1', remoteModelId: 'anthropic.claude-opus-4-5' }],
  } as unknown as AiConnectionDto

  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(updateAiConnection).mockReset()
    vi.mocked(listPermittedProviders).mockResolvedValue({ providers: [bedrock], isRestricted: false })
  })

  it('is absent from what a new connection is offered', async () => {
    await mountComposable()

    api.openCreateEditor()
    await nextTick()

    expect(api.authModeOptions.value.map((option) => option.value)).toEqual([bedrockSigV4])
    expect(api.editor.authMode).toBe(bedrockSigV4)
  })

  // Leaving it out here would open this profile on the other shape, and saving would then rewrite a credential
  // the operator never touched. The boxes it holds are rendered from the same declaration, so the form shows
  // what is stored rather than asking for a pair of values in place of one.
  it('is offered, selected and rendered when the profile being edited holds it', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([storedUnderThePackedPair])

    await mountComposable()

    api.openEditEditor(api.profiles.value[0])
    await nextTick()

    expect(api.editor.authMode).toBe(bedrockApiKey)
    expect(api.authModeOptions.value.map((option) => option.value)).toEqual([bedrockApiKey, bedrockSigV4])
    expect(api.credentialFields.value.map((field) => field.name)).toEqual(['apiKey'])
  })

  // The shape is kept for the profile that holds it, not for the form, so starting a new connection after
  // editing that profile offers the current shapes again.
  it('does not follow the profile that held it into a new connection', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([storedUnderThePackedPair])

    await mountComposable()

    api.openEditEditor(api.profiles.value[0])
    await nextTick()

    api.openCreateEditor()
    await nextTick()

    expect(api.authModeOptions.value.map((option) => option.value)).toEqual([bedrockSigV4])
    expect(api.editor.authMode).toBe(bedrockSigV4)
  })

  // The server accepts a shape it declares whatever its flag says, so the form must not refuse an edit to a
  // profile stored under one. The refusal is for a shape the family no longer declares at all.
  it('does not stand in the way of saving the profile that holds it', async () => {
    vi.mocked(listAiConnections).mockResolvedValue([storedUnderThePackedPair])
    vi.mocked(updateAiConnection).mockResolvedValue({} as never)

    await mountComposable()

    api.openEditEditor(api.profiles.value[0])
    await nextTick()

    await api.saveProfile()

    expect(api.saveError.value).toBe('')
    expect(vi.mocked(updateAiConnection)).toHaveBeenCalled()
  })
})

describe('what the form insists on before it saves a credential', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(updateAiConnection).mockReset()
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [
        {
          providerKind: 'awsBedrock',
          label: 'AWS Bedrock',
          declaredFields: [],
          isPermitted: true,
          protocolModes: [{ value: 'Auto', label: 'Auto' }],
          authModes: [{ value: 'awsBedrock:SigV4', label: 'AWS Signature v4' }],
          credentialFields: { 'awsBedrock:SigV4': [accessKeyIdField, secretAccessKeyField] },
        },
      ],
      isRestricted: false,
    })
  })

  async function openBedrockEdit() {
    await mountComposable()

    api.editor.providerKind = 'awsBedrock'
    api.editor.authMode = bedrockSigV4
    await nextTick()

    api.editor.mode = 'edit'
    api.editor.profileId = 'p1'
    api.editor.displayName = 'Bedrock'
    api.editor.baseUrl = 'https://bedrock-runtime.eu-central-1.amazonaws.com'
    api.addModel()
    api.editor.models[0].remoteModelId = 'anthropic.claude-3-5-sonnet'
  }

  // Entering one box replaces the whole stored credential, so a rotation that fills in the access key id and
  // leaves the secret access key blank sends a field map the server refuses. The operator is told which box is
  // still empty while looking at it.
  it('names the field still missing when only part of a credential was re-entered', async () => {
    await openBedrockEdit()
    api.editor.credentials = { accessKeyId: 'AKIAROTATED' }

    await api.saveProfile()

    expect(api.saveError.value).toContain('Secret access key')
    expect(vi.mocked(updateAiConnection)).not.toHaveBeenCalled()
  })

  // A credential nobody touched is the case the bypass exists for: the boxes start blank because a stored
  // credential is never returned, and an edit of anything else must not demand it back.
  it('saves an edit that left the credential untouched', async () => {
    await openBedrockEdit()
    api.editor.credentials = {}
    vi.mocked(updateAiConnection).mockResolvedValue({ id: 'p1' } as never)

    await api.saveProfile()

    expect(api.saveError.value).toBe('')
    expect(vi.mocked(updateAiConnection)).toHaveBeenCalledTimes(1)
    expect(vi.mocked(updateAiConnection).mock.calls[0][2].auth?.fields).toBeUndefined()
  })
})

// Everything the form says about a family — its name, the names of the shapes it offers, and what its boxes
// take — comes from the descriptor. A console holding its own copy could only name the families it shipped
// knowing about, which the add-in boundary exists to avoid.
describe('what the form knows about a family', () => {
  const contosoDescriptor = {
    providerKind: 'contosoLlm' as AiProviderKind,
    label: 'Contoso LLM',
    declaredFields: [],
    isPermitted: true,
    protocolModes: [
      { value: 'Auto' as const, label: 'Auto' },
      { value: 'contosoLlm:AnthropicMessages' as const, label: 'Contoso Messages' },
      { value: 'Embeddings' as const, label: 'Embeddings' },
    ],
    authModes: [{ value: 'contosoLlm:XApiKey' as const, label: 'Contoso key header' }],
    credentialFields: { 'contosoLlm:XApiKey': [apiKeyField] },
    connectionForm: {
      namePlaceholder: 'Contoso (prod)',
      baseUrlPlaceholder: 'https://llm.contoso.test/v1',
      baseUrlHint: 'The gateway your Contoso account was issued.',
      requiredQueryParam: 'account',
      queryParamPlaceholder: 'account=your-account',
    },
  }

  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(updateAiConnection).mockReset()
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providers: [contosoDescriptor],
      isRestricted: false,
    })
  })

  it('names a family this build has never heard of', async () => {
    await mountComposable()

    expect(api.availableProviderOptions.value).toEqual([{ value: 'contosoLlm', label: 'Contoso LLM' }])
    expect(api.providerLabel('contosoLlm' as AiProviderKind)).toBe('Contoso LLM')
  })

  it('names the credential and protocol modes that family offers', async () => {
    await mountComposable()

    expect(api.authModeOptions.value).toEqual([{ value: 'contosoLlm:XApiKey', label: 'Contoso key header' }])
    expect(api.authModeLabel('contosoLlm:XApiKey')).toBe('Contoso key header')
    expect(api.protocolModeOptions.value.map((option) => option.label)).toEqual([
      'Auto',
      'Contoso Messages',
      'Embeddings',
    ])
  })

  it('tells an operator what that family wants in its connection boxes', async () => {
    await mountComposable()

    expect(api.guidance.value.baseUrlPlaceholder).toBe('https://llm.contoso.test/v1')
    expect(api.guidance.value.baseUrlHint).toBe('The gateway your Contoso account was issued.')
    expect(api.guidance.value.requiredQueryParam).toBe('account')
  })

  // A model saved as speaking a shape its family cannot serve produces a call in the wrong wire format, which
  // the provider rejects with a message naming neither the model nor the shape.
  it('saves a model against the protocol modes its family declares', async () => {
    vi.mocked(updateAiConnection).mockResolvedValue({} as never)
    await mountComposable()

    api.editor.mode = 'edit'
    api.editor.profileId = 'p1'
    api.editor.displayName = 'Contoso'
    api.editor.baseUrl = 'https://llm.contoso.test/v1'
    api.addModel()
    api.editor.models[0].remoteModelId = 'contoso-large'
    api.addModel()
    api.editor.models[1].remoteModelId = 'contoso-embed'
    api.editor.models[1].kind = 'embedding'
    api.editor.models[1].tokenizerName = 'cl100k_base'
    api.editor.models[1].maxInputTokens = '8192'
    api.editor.models[1].embeddingDimensions = '1536'

    await api.saveProfile()

    const [, , request] = vi.mocked(updateAiConnection).mock.calls[0]
    expect(request.configuredModels![0].supportedProtocolModes)
      .toEqual(['Auto', 'contosoLlm:AnthropicMessages'])
    expect(request.configuredModels![1].supportedProtocolModes).toEqual(['Auto', 'Embeddings'])
  })
})
