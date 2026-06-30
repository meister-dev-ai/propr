// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent } from 'vue'
import { useProviderConnectionsViewModel, type ProviderConnectionsViewModel } from '@/features/provider-connections/view-models/useProviderConnectionsViewModel'
import type { ClientScmConnectionDto } from '@/services/providerConnectionsService'

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
    let vm!: ProviderConnectionsViewModel

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

  it('requires and submits userName for Azure DevOps Server windows auth', async () => {
    let vm!: ProviderConnectionsViewModel
    const createProviderConnection = vi.fn(async (): Promise<ClientScmConnectionDto> => ({
      id: 'connection-2',
      clientId: 'client-1',
      providerFamily: 'azureDevOps',
      hostBaseUrl: 'https://ado-server.example.com/tfs',
      authenticationKind: 'windowsUserAccount',
      userName: 'CONTOSO\\ado-user',
      displayName: 'Azure DevOps Server',
      isActive: true,
      verificationStatus: 'unknown',
      readinessLevel: 'configured',
      createdAt: '2026-05-01T00:00:00Z',
      updatedAt: '2026-05-01T00:00:00Z',
    } as ClientScmConnectionDto))

    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({
          clientId: 'client-1',
          providerConnectionsService: {
            listProviderActivationStatuses: async () => [{ providerFamily: 'azureDevOps', isEnabled: true } as never],
            listProviderConnections: async () => [],
            createProviderConnection,
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

    vm!.createForm.providerFamily = 'azureDevOps'
    vm!.createForm.hostBaseUrl = 'https://ado-server.example.com/tfs'
    vm!.createForm.authenticationKind = 'windowsUserAccount'
    vm!.createForm.displayName = 'Azure DevOps Server'
    vm!.createForm.secret = 'password'

    await vm!.handleCreateConnection()
    expect(vm!.createError.value).toContain('User name is required')

    vm!.createForm.userName = 'CONTOSO\\ado-user'
    await vm!.handleCreateConnection()

    expect(createProviderConnection).toHaveBeenCalledWith('client-1', expect.objectContaining({
      providerFamily: 'azureDevOps',
      authenticationKind: 'windowsUserAccount',
      userName: 'CONTOSO\\ado-user',
      hostBaseUrl: 'https://ado-server.example.com/tfs',
    }))
  })

  it('threads data-retention settings into the create payload', async () => {
    let vm!: ProviderConnectionsViewModel
    const createProviderConnection = vi.fn(async (): Promise<ClientScmConnectionDto> => ({
      id: 'connection-3',
      clientId: 'client-1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'GitHub',
      isActive: true,
      verificationStatus: 'unknown',
      createdAt: '2026-05-01T00:00:00Z',
      updatedAt: '2026-05-01T00:00:00Z',
    } as ClientScmConnectionDto))

    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({
          clientId: 'client-1',
          providerConnectionsService: {
            listProviderActivationStatuses: async () => [{ providerFamily: 'github', isEnabled: true } as never],
            listProviderConnections: async () => [],
            createProviderConnection,
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

    vm!.createForm.displayName = 'GitHub'
    vm!.createForm.secret = 'ghp_test'
    vm!.createForm.storeThreads = true
    vm!.createForm.storeDiffs = true
    vm!.createForm.retentionDays = '90'

    await vm!.handleCreateConnection()

    expect(createProviderConnection).toHaveBeenCalledWith('client-1', expect.objectContaining({
      storeThreads: true,
      storeDiffs: true,
      retentionDays: 90,
    }))
  })

  it('rejects an out-of-range retention value on create and submits a blank value as null', async () => {
    let vm!: ProviderConnectionsViewModel
    const createProviderConnection = vi.fn(async (): Promise<ClientScmConnectionDto> => ({
      id: 'connection-4',
      clientId: 'client-1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'GitHub',
      isActive: true,
      verificationStatus: 'unknown',
      createdAt: '2026-05-01T00:00:00Z',
      updatedAt: '2026-05-01T00:00:00Z',
    } as ClientScmConnectionDto))

    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({
          clientId: 'client-1',
          providerConnectionsService: {
            listProviderActivationStatuses: async () => [{ providerFamily: 'github', isEnabled: true } as never],
            listProviderConnections: async () => [],
            createProviderConnection,
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

    vm!.createForm.displayName = 'GitHub'
    vm!.createForm.secret = 'ghp_test'
    vm!.createForm.retentionDays = '0'

    await vm!.handleCreateConnection()
    expect(vm!.createError.value).toContain('Retention')
    expect(createProviderConnection).not.toHaveBeenCalled()

    vm!.createForm.retentionDays = ''
    await vm!.handleCreateConnection()

    expect(createProviderConnection).toHaveBeenCalledWith('client-1', expect.objectContaining({
      retentionDays: null,
    }))
  })

  it('threads data-retention settings into the patch payload', async () => {
    let vm!: ProviderConnectionsViewModel
    const connection: ClientScmConnectionDto = {
      id: 'connection-5',
      clientId: 'client-1',
      providerFamily: 'github',
      hostBaseUrl: 'https://github.com',
      authenticationKind: 'personalAccessToken',
      displayName: 'GitHub',
      isActive: true,
      verificationStatus: 'verified',
      storeThreads: false,
      storeDiffs: false,
      retentionDays: null,
      createdAt: '2026-05-01T00:00:00Z',
      updatedAt: '2026-05-01T00:00:00Z',
    }
    const updateProviderConnection = vi.fn(async (): Promise<ClientScmConnectionDto> => ({
      ...connection,
      storeThreads: true,
      storeDiffs: true,
      retentionDays: 45,
    }))

    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({
          clientId: 'client-1',
          providerConnectionsService: {
            listProviderActivationStatuses: async () => [{ providerFamily: 'github', isEnabled: true } as never],
            listProviderConnections: async () => [connection],
            createProviderConnection: async () => { throw new Error('unused') },
            updateProviderConnection,
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

    vm!.openConnectionDetail('connection-5')
    await flushPromises()

    // The edit form initializes from the connection's stored retention values.
    expect(vm!.editForm.storeThreads).toBe(false)
    expect(vm!.editForm.storeDiffs).toBe(false)
    expect(vm!.editForm.retentionDays).toBe('')

    vm!.editForm.storeThreads = true
    vm!.editForm.storeDiffs = true
    vm!.editForm.retentionDays = '45'

    await vm!.handleSaveConnectionEdit('connection-5')

    expect(updateProviderConnection).toHaveBeenCalledWith('client-1', 'connection-5', expect.objectContaining({
      storeThreads: true,
      storeDiffs: true,
      retentionDays: 45,
    }))
  })
})
