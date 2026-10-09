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
  it.each(['https://dev.azure.com', 'https://organization.visualstudio.com'])('keeps the implicit OAuth default while typing a new Azure DevOps Services host %s', async (host) => {
    let vm!: ProviderConnectionsViewModel
    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({ clientId: 'client-1', autoLoad: false })
        return () => null
      },
    }))
    app.mount(document.createElement('div'))
    try {
      vm.createForm.providerFamily = 'azureDevOps'
      await flushPromises()
      expect(vm.createForm.authenticationKind).toBe('oauthClientCredentials')
      vm.createForm.oAuthTenantId = 'synthetic-tenant'
      vm.createForm.oAuthClientId = 'synthetic-client'
      vm.createForm.secret = 'synthetic-client-secret'

      for (const nextHost of ['', ...Array.from(host, (_, index) => host.slice(0, index + 1))]) {
        vm.createForm.hostBaseUrl = nextHost
        await flushPromises()
        expect(vm.createForm.authenticationKind).toBe('oauthClientCredentials')
        expect(vm.createForm.oAuthTenantId).toBe('synthetic-tenant')
        expect(vm.createForm.oAuthClientId).toBe('synthetic-client')
        expect(vm.createForm.secret).toBe('synthetic-client-secret')
      }
    } finally {
      app.unmount()
    }
  })

  it.each([
    ['createForm', 'https://dev.azure.com'], ['createForm', 'https://organization.visualstudio.com'],
    ['editForm', 'https://dev.azure.com'], ['editForm', 'https://organization.visualstudio.com'],
  ] as const)('preserves OAuth metadata while clearing and typing %s host %s', async (formName, host) => {
    let vm!: ProviderConnectionsViewModel
    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({ clientId: 'client-1', autoLoad: false })
        return () => null
      },
    }))
    app.mount(document.createElement('div'))
    try {
      const form = vm[formName]
      form.providerFamily = 'azureDevOps'
      await flushPromises()
      form.hostBaseUrl = host
      form.authenticationKind = 'oauthClientCredentials'
      await flushPromises()
      form.oAuthTenantId = 'synthetic-tenant'
      form.oAuthClientId = 'synthetic-client'
      for (const nextHost of ['', ...Array.from(host, (_, index) => host.slice(0, index + 1)), '', host]) {
        form.hostBaseUrl = nextHost
        await flushPromises()
        expect(form.authenticationKind).toBe('oauthClientCredentials')
        expect(form.oAuthTenantId).toBe('synthetic-tenant')
        expect(form.oAuthClientId).toBe('synthetic-client')
      }
    } finally {
      app.unmount()
    }
  })

  it.each(['createForm', 'editForm'] as const)('retains explicit PAT through Services host typing in %s', async (formName) => {
    let vm!: ProviderConnectionsViewModel
    const app = createApp(defineComponent({
      setup() {
        vm = useProviderConnectionsViewModel({ clientId: 'client-1', autoLoad: false })
        return () => null
      },
    }))
    app.mount(document.createElement('div'))
    try {
      const form = vm[formName]
      form.providerFamily = 'azureDevOps'
      await flushPromises()
      form.authenticationKind = 'personalAccessToken'
      await flushPromises()
      for (const host of ['https://dev.azure.com', 'https://organization.visualstudio.com']) {
        for (const nextHost of ['', ...Array.from(host, (_, index) => host.slice(0, index + 1))]) {
          form.hostBaseUrl = nextHost
          await flushPromises()
          expect(form.authenticationKind).toBe('personalAccessToken')
        }
      }
    } finally {
      app.unmount()
    }
  })

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
