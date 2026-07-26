// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { listAiConnections, listPermittedProviders } from '@/services/aiConnectionsService'
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

describe('which provider families the form offers', () => {
  beforeEach(() => {
    vi.mocked(listAiConnections).mockResolvedValue([])
    vi.mocked(listPermittedProviders).mockReset()
  })

  // The provider enum names families this build has no driver for. The server decides what is offerable, so a
  // named-but-unimplemented family must never reach the picker — otherwise opening the enum moves the failure to
  // review time, which is the whole thing #148 was supposed to avoid.
  it('offers only what the server says this client can configure', async () => {
    vi.mocked(listPermittedProviders).mockResolvedValue({
      providerKinds: ['azureOpenAi', 'openAiCompatible'],
      isRestricted: false,
      implementedKinds: ['azureOpenAi', 'openAi', 'liteLlm', 'openAiCompatible'],
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
      providerKinds: ['azureOpenAi'],
      isRestricted: true,
      implementedKinds: ['azureOpenAi', 'openAi', 'liteLlm', 'openAiCompatible'],
    })

    await mountComposable()

    expect(api.providerUnavailableReason('liteLlm')).toContain('does not permit')
    expect(api.providerUnavailableReason('anthropic')).toContain('no driver')
    expect(api.providerUnavailableReason('azureOpenAi')).toBe('')
  })

  // A failed lookup must not lock an operator out of the form; the server still refuses anything invalid.
  it('falls back to offering everything when the lookup fails', async () => {
    vi.mocked(listPermittedProviders).mockRejectedValue(new Error('nope'))

    await mountComposable()

    expect(api.availableProviderOptions.value.length).toBeGreaterThan(0)
    expect(api.isProviderPermitted('azureOpenAi')).toBe(true)
  })
})
