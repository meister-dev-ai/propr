// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent } from 'vue'
import { useProviderConnectionsViewModel } from '@/features/provider-connections/view-models/useProviderConnectionsViewModel'

const notifyMock = vi.fn()

vi.mock('@/composables/useNotification', () => ({
  useNotification: () => ({ notify: notifyMock }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    capabilities: { value: [{ key: 'multiple-scm-providers', isAvailable: true, message: null }] },
  }),
}))

describe('useProviderConnectionsViewModel', () => {
  beforeEach(() => {
    notifyMock.mockReset()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads provider options and connections on mount', async () => {
    let detailOpen = false
    let vm: ReturnType<typeof useProviderConnectionsViewModel> | null = null

    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({
          clientId: 'client-1',
          onDetailOpenChange: (value) => {
            detailOpen = value
          },
          providerConnectionsService: {
            listProviderActivationStatuses: async () => [{ providerFamily: 'github', isEnabled: true } as never],
            listProviderConnections: async () => [{
              id: 'connection-1',
              clientId: 'client-1',
              providerFamily: 'github',
              hostBaseUrl: 'https://github.com',
              authenticationKind: 'personalAccessToken',
              displayName: 'GitHub',
              isActive: true,
              verificationStatus: 'verified',
              readinessLevel: 'workflowComplete',
              createdAt: '2026-05-01T00:00:00Z',
              updatedAt: '2026-05-01T00:00:00Z',
            } as never],
            createProviderConnection: async () => { throw new Error('unused') },
            updateProviderConnection: async () => { throw new Error('unused') },
            verifyProviderConnection: async () => { throw new Error('unused') },
            deleteProviderConnection: async () => undefined,
            listProviderScopes: async () => [],
            createProviderScope: async () => { throw new Error('unused') },
            updateProviderScope: async () => { throw new Error('unused') },
            deleteProviderScope: async () => undefined,
            resolveReviewerIdentityCandidates: async () => [],
            getReviewerIdentity: async () => null,
            setReviewerIdentity: async () => { throw new Error('unused') },
            deleteReviewerIdentity: async () => undefined,
          },
        })
        return () => null
      },
    }))

    app.mount(document.createElement('div'))
    await flushPromises()

    expect(vm?.providerOptions.value).toEqual([{ value: 'github', label: 'GitHub' }])
    expect(vm?.connections.value).toHaveLength(1)

    vm?.openConnectionDetail('connection-1')
    await flushPromises()

    expect(detailOpen).toBe(true)
    expect(vm?.selectedConnection.value?.displayName).toBe('GitHub')
  })
})
