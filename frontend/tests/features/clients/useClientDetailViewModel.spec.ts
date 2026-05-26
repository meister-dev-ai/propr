// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockGet = vi.fn()
const mockPatch = vi.fn()
const mockDelete = vi.fn()
const mockRouterPush = vi.fn()
const mockRouterReplace = vi.fn()
const mockRoute = {
  params: { id: 'client-1' },
  query: {} as Record<string, string>,
}
const hasClientRoleMock = vi.fn((_clientId: string, minRole: 0 | 1) => minRole <= 1)
const getCapabilityMock = vi.fn((key: string) => ({ key, isAvailable: true, message: null }))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: mockRouterPush, replace: mockRouterReplace }),
  useRoute: () => mockRoute,
}))

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: mockGet, PATCH: mockPatch, DELETE: mockDelete }),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: hasClientRoleMock,
    getCapability: getCapabilityMock,
  }),
}))

import { useClientDetailViewModel } from '@/features/clients/view-models/useClientDetailViewModel'

const sampleClient = {
  id: 'client-1',
  displayName: 'Acme Review Team',
  isActive: true,
  createdAt: '2026-04-25T10:00:00Z',
  defaultReviewStrategy: 'fileByFile',
  scmCommentPostingEnabled: true,
  enableProRV: true,
}

describe('useClientDetailViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockRoute.query = {}
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: 0 | 1) => minRole <= 1)
    getCapabilityMock.mockImplementation((key: string) => ({ key, isAvailable: true, message: null }))
    mockGet.mockResolvedValue({ data: sampleClient, response: { status: 200, ok: true } })
    mockPatch.mockResolvedValue({ data: sampleClient, response: { ok: true } })
    mockDelete.mockResolvedValue({ response: { ok: true } })
  })

  it('loads the client on mount and initializes editable fields', async () => {
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    expect(mockGet).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
    })
    expect(vm.client.value?.displayName).toBe('Acme Review Team')
    expect(vm.editedDisplayName.value).toBe('Acme Review Team')
    expect(vm.activeTab.value).toBe('config')
  })

  it('keeps data loading under the explicit loadClient seam when autoLoad is disabled', () => {
    useClientDetailViewModel({ autoLoad: false })

    expect(mockGet).not.toHaveBeenCalled()
  })

  it('honors a direct tab query when the tab is available', async () => {
    mockRoute.query = { tab: 'procursor' }
    const vm = useClientDetailViewModel({ autoLoad: false })

    expect(vm.activeTab.value).toBe('procursor')
  })

  it('limits available tabs for read-only client users', async () => {
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: 0 | 1) => minRole === 0)
    const vm = useClientDetailViewModel({ autoLoad: false })

    expect(vm.availableTabs.value).toContain('history')
    expect(vm.availableTabs.value).toContain('usage')
    expect(vm.availableTabs.value).not.toContain('config')
    expect(vm.activeTab.value).toBe('history')
  })

  it('saves the display name through the patch endpoint', async () => {
    mockPatch.mockResolvedValue({ data: { ...sampleClient, displayName: 'New Name' }, response: { ok: true } })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()
    vm.editedDisplayName.value = 'New Name'

    await vm.saveDisplayName()

    expect(mockPatch).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: { displayName: 'New Name' },
    })
    expect(vm.client.value?.displayName).toBe('New Name')
  })

  it('saves advanced settings with strategy, scm posting, and ProRV state', async () => {
    mockPatch.mockResolvedValue({
      data: { ...sampleClient, defaultReviewStrategy: 'prWideAgentic', scmCommentPostingEnabled: false, enableProRV: false },
      response: { ok: true },
    })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()
    vm.editedDefaultReviewStrategy.value = 'prWideAgentic'
    vm.editedScmCommentPostingEnabled.value = false
    vm.editedEnableProRV.value = false

    await vm.saveAdvancedSettings()

    expect(mockPatch).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: {
        defaultReviewStrategy: 'prWideAgentic',
        scmCommentPostingEnabled: false,
        enableProRV: false,
      },
    })
  })

  it('navigates back to clients when the detail record is not found', async () => {
    mockGet.mockResolvedValue({ data: null, response: { status: 404, ok: false } })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    expect(vm.notFound.value).toBe(true)
    expect(mockRouterPush).toHaveBeenCalledWith({ name: 'clients' })
  })
})
