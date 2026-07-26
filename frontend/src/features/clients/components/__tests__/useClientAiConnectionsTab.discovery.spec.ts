// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { discoverAiModels, listAiConnections, listPermittedProviders } from '@/services/aiConnectionsService'
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

describe('model discovery reporting', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(listPermittedProviders).mockResolvedValue({ providers: [], isRestricted: false })
    await mountComposable()
    api.editor.baseUrl = 'https://bedrock-runtime.eu-central-1.amazonaws.com'
    api.editor.providerKind = 'awsBedrock'
  })

  // A driver's warnings are the actionable half of a successful discovery: that many Bedrock models answer only
  // through an inference profile is something an operator can act on, and a count alone is not.
  it('reports the provider warnings alongside the count', async () => {
    vi.mocked(discoverAiModels).mockResolvedValue({
      discoveryStatus: 'succeeded',
      manualEntryAllowed: true,
      warnings: ['Some Bedrock models can only be called through an inference profile.'],
      models: [{ id: 'm1', remoteModelId: 'anthropic.claude-opus-4-5', displayName: 'Claude Opus 4.5', supportsChat: true }],
    } as never)

    await api.handleDiscoverModels()

    expect(api.discoveryMessage.value).toContain('Discovered 1 model.')
    expect(api.discoveryMessage.value).toContain('inference profile')
  })

  it('says only what it found when the provider warned about nothing', async () => {
    vi.mocked(discoverAiModels).mockResolvedValue({
      discoveryStatus: 'succeeded',
      manualEntryAllowed: true,
      warnings: [],
      models: [{ id: 'm1', remoteModelId: 'gpt-4o', displayName: 'GPT-4o', supportsChat: true }],
    } as never)

    await api.handleDiscoverModels()

    expect(api.discoveryMessage.value).toBe('Discovered 1 model.')
  })

  it('leads with the reason when discovery failed', async () => {
    vi.mocked(discoverAiModels).mockResolvedValue({
      discoveryStatus: 'failed',
      manualEntryAllowed: true,
      warnings: ['AccessDeniedException: not authorized to call ListFoundationModels.'],
      models: [],
    } as never)

    await api.handleDiscoverModels()

    expect(api.discoveryMessage.value).toContain('AccessDeniedException')
  })
})
