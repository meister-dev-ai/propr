// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiConnectionDto, AiModelDiscoveryResultDto } from '@/services/aiConnectionsService'
import { discoverAiModels, listAiConnections, updateAiConnection } from '@/services/aiConnectionsService'
import type { EditableModel } from '../aiConnectionsForm.types'
import { useClientAiConnectionsTab } from '../useClientAiConnectionsTab'

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

const profile = {
  id: 'profile-1',
  displayName: 'OpenAI',
  providerKind: 'openAi',
  baseUrl: 'https://api.openai.com/v1',
  authMode: 'apiKey',
  configuredModels: [
    {
      id: 'm1',
      remoteModelId: 'gpt-6-luna',
      displayName: 'GPT-6 Luna',
      reasoningLevels: ['none', 'low', 'medium', 'high'],
      reasoningLevelsSource: 'addIn',
      defaultReasoningLevel: 'none',
    },
    {
      id: 'm2',
      remoteModelId: 'gpt-6.1-sol',
      displayName: 'GPT-6.1 Sol',
      reasoningLevels: ['low', 'medium', 'high'],
      reasoningLevelsSource: 'addIn',
      defaultReasoningLevel: 'high',
    },
    { id: 'm3', remoteModelId: 'o3', displayName: 'o3' },
  ],
  purposeBindings: [],
} as unknown as AiConnectionDto

// A call that no other place chooses a level for takes the default level of the model, so the level has to survive
// opening and saving the connection.
describe('useClientAiConnectionsTab default reasoning level', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(updateAiConnection).mockReset()
  })

  it('reads the default level of each model', async () => {
    await mountComposable()

    api.openEditEditor(profile)

    expect(api.editor.models.map((model) => model.defaultReasoningLevel)).toEqual(['none', 'high', null])
  })

  it('starts a new model without a default level', async () => {
    await mountComposable()

    api.addModel()

    expect(api.editor.models[0]!.defaultReasoningLevel).toBeNull()
  })

  // The request states the default level of every chat model, null included, because the save stores what the request
  // states. An embedding model takes no level.
  it('saves the default level of a chat model and leaves it out for an embedding model', async () => {
    vi.mocked(updateAiConnection).mockResolvedValue({ id: 'profile-1' } as AiConnectionDto)
    await mountComposable()
    api.editor.mode = 'edit'
    api.editor.profileId = 'profile-1'
    api.editor.displayName = 'Azure Test'
    api.editor.providerKind = 'azureOpenAi'
    api.editor.baseUrl = 'https://example.openai.azure.com'
    api.editor.authMode = 'apiKey'
    api.editor.credentials = {}
    api.editor.bindings = []
    api.editor.models = [
      editableModel({ localId: 'local-1', remoteModelId: 'gpt-6-luna', kind: 'chat', defaultReasoningLevel: 'high' }),
      editableModel({ localId: 'local-2', remoteModelId: 'gpt-6-sol', kind: 'chat', defaultReasoningLevel: null }),
      editableModel({
        localId: 'local-3',
        remoteModelId: 'text-embedding-3-small',
        kind: 'embedding',
        tokenizerName: 'cl100k_base',
        maxInputTokens: '8191',
        embeddingDimensions: '1536',
        defaultReasoningLevel: 'high',
      }),
    ]

    await api.saveProfile()
    await flushPromises()

    expect(updateAiConnection).toHaveBeenCalledTimes(1)
    const [, , request] = vi.mocked(updateAiConnection).mock.calls[0]!
    expect(request.configuredModels!.map((model) => model.defaultReasoningLevel)).toEqual(['high', null, undefined])
  })

  // The server adds the default level of a model to detected levels that lack it, so the form does the same when the
  // models are discovered again, and the default level stays.
  it('keeps the default level when the models are discovered again and adds it to discovered levels that lack it', async () => {
    discover([
      { remoteModelId: 'gpt-6-luna', displayName: 'GPT-6 Luna', supportsEmbedding: false, reasoningLevels: ['none', 'low', 'max'], reasoningLevelsSource: 'provider' },
      { remoteModelId: 'gpt-6.1-sol', displayName: 'GPT-6.1 Sol', supportsEmbedding: false, reasoningLevels: ['low', 'medium'], reasoningLevelsSource: 'provider' },
    ])
    await mountComposable()
    api.openEditEditor(profile)

    await api.handleDiscoverModels()
    await flushPromises()

    expect(modelState('gpt-6-luna')).toEqual({ levels: ['none', 'low', 'max'], source: 'provider', defaultLevel: 'none' })
    expect(modelState('gpt-6.1-sol')).toEqual({ levels: ['low', 'medium', 'high'], source: 'provider', defaultLevel: 'high' })
  })

  it('gives a newly discovered model the default level that the discovery reports for it', async () => {
    discover([
      { remoteModelId: 'gpt-6-terra', displayName: 'GPT-6 Terra', supportsEmbedding: false, reasoningLevels: ['none', 'high'], reasoningLevelsSource: 'provider', defaultReasoningLevel: 'high' },
      { remoteModelId: 'gpt-6-nova', displayName: 'GPT-6 Nova', supportsEmbedding: false, reasoningLevels: ['low', 'high'], reasoningLevelsSource: 'provider' },
    ])
    await mountComposable()
    api.openEditEditor(profile)

    await api.handleDiscoverModels()
    await flushPromises()

    expect(modelState('gpt-6-terra').defaultLevel).toBe('high')
    expect(modelState('gpt-6-nova').defaultLevel).toBeNull()
  })

  // A model in the form keeps its stored default level when the discovery reports another, even when the stored level is
  // empty, because an operator may have left it empty on purpose.
  it('keeps the stored default level of a model when the discovery reports another', async () => {
    discover([
      { remoteModelId: 'gpt-6-luna', displayName: 'GPT-6 Luna', supportsEmbedding: false, reasoningLevels: ['none', 'low', 'high'], reasoningLevelsSource: 'provider', defaultReasoningLevel: 'high' },
      { remoteModelId: 'o3', displayName: 'o3', supportsEmbedding: false, reasoningLevels: ['low', 'medium', 'high'], reasoningLevelsSource: 'provider', defaultReasoningLevel: 'medium' },
    ])
    await mountComposable()
    api.openEditEditor(profile)

    await api.handleDiscoverModels()
    await flushPromises()

    expect(modelState('gpt-6-luna')).toEqual({ levels: ['none', 'low', 'high'], source: 'provider', defaultLevel: 'none' })
    expect(modelState('o3')).toEqual({ levels: ['low', 'medium', 'high'], source: 'provider', defaultLevel: null })
  })
})

function discover(models: NonNullable<AiModelDiscoveryResultDto['models']>): void {
  vi.mocked(discoverAiModels).mockResolvedValue({ discoveryStatus: 'succeeded', models } as AiModelDiscoveryResultDto)
}

function modelState(remoteModelId: string) {
  const model = api.editor.models.find((candidate) => candidate.remoteModelId === remoteModelId)!
  return { levels: model.reasoningLevels, source: model.reasoningLevelsSource, defaultLevel: model.defaultReasoningLevel }
}

function editableModel(overrides: Partial<EditableModel>): EditableModel {
  return {
    localId: 'local',
    existingId: null,
    remoteModelId: '',
    displayName: '',
    kind: 'chat',
    tokenizerName: '',
    maxInputTokens: '',
    maxContextTokens: '',
    embeddingDimensions: '',
    supportsStructuredOutput: true,
    supportsToolUse: true,
    inputCostPer1MUsd: '',
    outputCostPer1MUsd: '',
    cachedInputCostPer1MUsd: '',
    cacheWriteCostPer1MUsd: '',
    supportsReasoning: false,
    supportsPromptCaching: false,
    reasoningContentField: '',
    defaultReasoningLevel: null,
    reasoningLevels: ['low', 'medium', 'high'],
    reasoningLevelsSource: 'operator',
    detectReasoningLevels: false,
    ...overrides,
  }
}
