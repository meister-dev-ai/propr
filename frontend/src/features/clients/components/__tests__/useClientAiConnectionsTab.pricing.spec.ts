// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import { createAiConnection, listAiConnections, updateAiConnection } from '@/services/aiConnectionsService'
import type { EditableModel } from '../aiConnectionsForm.types'
import { useClientAiConnectionsTab } from '../useClientAiConnectionsTab'

// The composable reaches straight for the service module, so the whole surface is mocked;
// only the three calls these tests exercise (load list, create, update) carry behavior.
vi.mock('@/services/aiConnectionsService', () => ({
  listAiConnections: vi.fn(),
  createAiConnection: vi.fn(),
  updateAiConnection: vi.fn(),
  discoverAiModels: vi.fn(),
  verifyAiConnection: vi.fn(),
  activateAiConnection: vi.fn(),
  deactivateAiConnection: vi.fn(),
  deleteAiConnection: vi.fn(),
}))

// Captured from the host component's setup so tests drive the composable directly.
let api!: ReturnType<typeof useClientAiConnectionsTab>

// Mounts a throwaway host whose setup exposes the composable. Mounting is required so
// onMounted runs its initial profile refresh against the mocked list call.
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

// Minimal editor state that clears validateEditor(): a name, a base URL, one chat model with a
// remote id, and no enabled purpose bindings (so no binding needs a selected model).
function primeValidEditor(options: { mode: 'create' | 'edit'; profileId?: string; apiKey?: string }) {
  api.editor.mode = options.mode
  api.editor.profileId = options.profileId ?? ''
  api.editor.displayName = 'Azure Test'
  api.editor.baseUrl = 'https://example.openai.azure.com'
  api.editor.authMode = 'apiKey'
  api.editor.apiKey = options.apiKey ?? ''
  api.editor.bindings = []
}

function chatModel(costs: Pick<EditableModel, 'inputCostPer1MUsd' | 'outputCostPer1MUsd' | 'cachedInputCostPer1MUsd'>): EditableModel {
  return {
    localId: 'local-1',
    existingId: null,
    remoteModelId: 'gpt-x',
    displayName: 'GPT X',
    kind: 'chat',
    tokenizerName: '',
    maxInputTokens: '',
    maxContextTokens: '',
    embeddingDimensions: '',
    supportsStructuredOutput: true,
    supportsToolUse: true,
    ...costs,
  }
}

describe('useClientAiConnectionsTab per-model USD pricing', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
  })

  it('loads configured-model USD costs into editor strings, blanking null and absent costs', async () => {
    await mountComposable()

    const profile = {
      id: 'profile-1',
      displayName: 'Azure',
      baseUrl: 'https://example.openai.azure.com',
      configuredModels: [
        { id: 'm1', remoteModelId: 'gpt-x', displayName: 'GPT X', inputCostPer1MUsd: 2.5, outputCostPer1MUsd: 10, cachedInputCostPer1MUsd: 1.25 },
        // Explicit null (input) and absent (output, cached) must all read back as empty strings.
        { id: 'm2', remoteModelId: 'gpt-y', displayName: 'GPT Y', inputCostPer1MUsd: null },
      ],
    } as unknown as AiConnectionDto

    api.openEditEditor(profile)

    expect(api.editor.models[0].inputCostPer1MUsd).toBe('2.5')
    expect(api.editor.models[0].outputCostPer1MUsd).toBe('10')
    expect(api.editor.models[0].cachedInputCostPer1MUsd).toBe('1.25')

    expect(api.editor.models[1].inputCostPer1MUsd).toBe('')
    expect(api.editor.models[1].outputCostPer1MUsd).toBe('')
    expect(api.editor.models[1].cachedInputCostPer1MUsd).toBe('')
  })

  it('serializes USD cost strings to numbers when saving an existing profile', async () => {
    vi.mocked(updateAiConnection).mockResolvedValue({ id: 'profile-1' } as AiConnectionDto)
    await mountComposable()

    primeValidEditor({ mode: 'edit', profileId: 'profile-1' })
    api.editor.models = [chatModel({ inputCostPer1MUsd: '2.5', outputCostPer1MUsd: '10', cachedInputCostPer1MUsd: '1.25' })]

    await api.saveProfile()
    await flushPromises()

    expect(updateAiConnection).toHaveBeenCalledTimes(1)
    const [, , request] = vi.mocked(updateAiConnection).mock.calls[0]
    const model = request.configuredModels![0]
    expect(model.inputCostPer1MUsd).toBe(2.5)
    expect(model.outputCostPer1MUsd).toBe(10)
    expect(model.cachedInputCostPer1MUsd).toBe(1.25)
  })

  it('serializes a blank cost to undefined and a literal zero to 0 on create', async () => {
    vi.mocked(createAiConnection).mockResolvedValue({ id: 'profile-2' } as AiConnectionDto)
    await mountComposable()

    primeValidEditor({ mode: 'create', apiKey: 'secret' })
    // Blank input stays unpriced; a literal '0' is a genuine free model and must survive as 0.
    api.editor.models = [chatModel({ inputCostPer1MUsd: '', outputCostPer1MUsd: '0', cachedInputCostPer1MUsd: '2.5' })]

    await api.saveProfile()
    await flushPromises()

    expect(createAiConnection).toHaveBeenCalledTimes(1)
    const [, request] = vi.mocked(createAiConnection).mock.calls[0]
    const model = request.configuredModels![0]
    expect(model.inputCostPer1MUsd).toBeUndefined()
    expect(model.outputCostPer1MUsd).toBe(0)
    expect(model.cachedInputCostPer1MUsd).toBe(2.5)
  })
})
