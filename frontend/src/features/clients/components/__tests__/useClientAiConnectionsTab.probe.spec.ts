// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { listAiConnections, listPermittedProviders, probeAiConnection } from '@/services/aiConnectionsService'
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

describe('probing a connection before saving it', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providerKinds: ['azureOpenAi', 'openAi', 'liteLlm', 'openAiCompatible'],
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
