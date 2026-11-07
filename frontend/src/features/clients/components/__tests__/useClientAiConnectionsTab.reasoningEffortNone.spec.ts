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
    { id: 'm1', remoteModelId: 'gpt-6-luna', displayName: 'GPT-6 Luna', supportsReasoningEffortNone: true },
    { id: 'm2', remoteModelId: 'o3', displayName: 'o3' },
  ],
  purposeBindings: [],
} as unknown as AiConnectionDto

// A pass configured for no reasoning sends the effort none only to a model with this flag, so the flag has to
// survive opening and saving the connection.
describe('useClientAiConnectionsTab reasoning effort none', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
  })

  it('reads whether each model accepts the reasoning effort none', async () => {
    await mountComposable()

    api.openEditEditor(profile)

    expect(api.editor.models.map((model) => model.supportsReasoningEffortNone)).toEqual([true, false])
  })

  it('saves the flag of a chat model and clears it for an embedding model', async () => {
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
      editableModel({ localId: 'local-1', remoteModelId: 'gpt-6-luna', kind: 'chat' }),
      editableModel({
        localId: 'local-2',
        remoteModelId: 'text-embedding-3-small',
        kind: 'embedding',
        tokenizerName: 'cl100k_base',
        maxInputTokens: '8191',
        embeddingDimensions: '1536',
      }),
    ]

    await api.saveProfile()
    await flushPromises()

    expect(updateAiConnection).toHaveBeenCalledTimes(1)
    const [, , request] = vi.mocked(updateAiConnection).mock.calls[0]
    expect(request.configuredModels!.map((model) => model.supportsReasoningEffortNone)).toEqual([true, false])
  })

  // Model discovery never reports the setting. A model that is already in the form keeps the value the operator set
  // when the models are discovered again, and a newly discovered model starts without it.
  it('keeps the operator setting when the models are discovered again', async () => {
    vi.mocked(discoverAiModels).mockResolvedValue({
      discoveryStatus: 'succeeded',
      models: [
        { remoteModelId: 'gpt-6-luna', displayName: 'GPT-6 Luna', supportsEmbedding: false },
        { remoteModelId: 'gpt-6-sol', displayName: 'GPT-6 Sol', supportsEmbedding: false },
      ],
    } as AiModelDiscoveryResultDto)
    await mountComposable()
    api.openEditEditor(profile)

    await api.handleDiscoverModels()
    await flushPromises()

    const settings = Object.fromEntries(
      api.editor.models.map((model) => [model.remoteModelId, model.supportsReasoningEffortNone]),
    )
    expect(settings).toEqual({ 'gpt-6-luna': true, o3: false, 'gpt-6-sol': false })
  })
})

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
    supportsReasoningEffortNone: true,
    ...overrides,
  }
}
