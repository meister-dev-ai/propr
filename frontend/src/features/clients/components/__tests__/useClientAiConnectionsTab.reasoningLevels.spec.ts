// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiConnectionDto, AiModelDiscoveryResultDto } from '@/services/aiConnectionsService'
import { discoverAiModels, listAiConnections, updateAiConnection } from '@/services/aiConnectionsService'
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
    { id: 'm1', remoteModelId: 'gpt-6.1-terra', displayName: 'Terra', reasoningLevels: ['none', 'low', 'max'], reasoningLevelsSource: 'addIn' },
    { id: 'm2', remoteModelId: 'gpt-6.1-sol', displayName: 'Sol', reasoningLevels: ['high'], reasoningLevelsSource: 'operator' },
    { id: 'm3', remoteModelId: 'private-model', displayName: 'Private' },
  ],
  purposeBindings: [],
} as unknown as AiConnectionDto

describe('useClientAiConnectionsTab reasoning levels', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(updateAiConnection).mockReset()
  })

  it('reads the levels of each model with their source', async () => {
    await mountComposable()

    api.openEditEditor(profile)

    expect(api.editor.models.map((model) => [model.reasoningLevels, model.reasoningLevelsSource])).toEqual([
      [['none', 'low', 'max'], 'addIn'],
      [['high'], 'operator'],
      [null, null],
    ])
  })

  // A list that no source filled is left out, so the save keeps what is stored or fills detected levels.
  it('saves the levels with their source and leaves out a list that no source filled', async () => {
    vi.mocked(updateAiConnection).mockResolvedValue({ id: 'profile-1' } as AiConnectionDto)
    await mountComposable()
    api.openEditEditor(profile)
    api.editor.models[2]!.detectReasoningLevels = true

    await api.saveProfile()
    await flushPromises()

    const [, , request] = vi.mocked(updateAiConnection).mock.calls[0]!
    expect(request.configuredModels!.map((model) => [model.reasoningLevels, model.reasoningLevelsSource, model.detectReasoningLevels])).toEqual([
      [['none', 'low', 'max'], 'addIn', undefined],
      [['high'], 'operator', undefined],
      [undefined, undefined, true],
    ])
  })

  // Levels an operator entered stay when the models are discovered again; detected levels follow the discovery.
  it('keeps levels an operator entered when the models are discovered again', async () => {
    vi.mocked(discoverAiModels).mockResolvedValue({
      discoveryStatus: 'succeeded',
      models: [
        { remoteModelId: 'gpt-6.1-terra', displayName: 'Terra', supportsEmbedding: false, reasoningLevels: ['low', 'high'], reasoningLevelsSource: 'provider' },
        { remoteModelId: 'gpt-6.1-sol', displayName: 'Sol', supportsEmbedding: false, reasoningLevels: ['low', 'max'], reasoningLevelsSource: 'provider' },
      ],
    } as AiModelDiscoveryResultDto)
    await mountComposable()
    api.openEditEditor(profile)

    await api.handleDiscoverModels()
    await flushPromises()

    const levels = Object.fromEntries(api.editor.models.map((model) => [model.remoteModelId, model.reasoningLevels]))
    expect(levels['gpt-6.1-terra']).toEqual(['low', 'high'])
    expect(levels['gpt-6.1-sol']).toEqual(['high'])
  })
})
