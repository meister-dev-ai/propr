// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const mockGet = vi.fn()
const mockPatch = vi.fn()
const mockPut = vi.fn()
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
  createAdminClient: () => ({ GET: mockGet, PATCH: mockPatch, PUT: mockPut, DELETE: mockDelete }),
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
  defaultReviewPipelineProfileId: 'file-by-file-balanced',
  defaultReviewPipelineProfileUpdatedAtUtc: null,
  scmCommentPostingEnabled: true,
  enableEvidenceBackedVerification: false,
  enableMultiPassUnion: false,
  includeLinkedItemsInContext: true,
}

const sampleReviewProfiles = {
  profiles: [
    { profileId: 'file-by-file-calm', displayName: 'Calm', isDefault: false },
    { profileId: 'file-by-file-balanced', displayName: 'Balanced', isDefault: true },
    { profileId: 'file-by-file-assertive', displayName: 'Assertive', isDefault: false },
  ],
}

const sampleClientReviewProfile = {
  clientId: 'client-1',
  defaultReviewPipelineProfileId: 'file-by-file-balanced',
  source: 'systemDefault' as const,
  updatedAtUtc: null,
}

describe('useClientDetailViewModel (FR-007, FR-008, FR-012)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mockRoute.query = {}
    hasClientRoleMock.mockImplementation((_clientId: string, minRole: 0 | 1) => minRole <= 1)
    getCapabilityMock.mockImplementation((key: string) => ({ key, isAvailable: true, message: null }))
    mockGet.mockReset()
    mockGet
      .mockResolvedValueOnce({ data: sampleClient, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleReviewProfiles, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleClientReviewProfile, response: { status: 200, ok: true } })
    mockPatch.mockResolvedValue({ data: sampleClient, response: { ok: true } })
    mockPut.mockResolvedValue({ data: sampleClientReviewProfile, response: { ok: true } })
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
    expect(vm.reviewProfiles.value).toHaveLength(3)
    expect(vm.clientReviewProfile.value?.defaultReviewPipelineProfileId).toBe('file-by-file-balanced')
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

  it('saves advanced settings with scm posting state', async () => {
    mockGet.mockReset()
    mockGet
      .mockResolvedValueOnce({ data: sampleClient, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleReviewProfiles, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleClientReviewProfile, response: { status: 200, ok: true } })
    mockPatch.mockResolvedValue({
      data: { ...sampleClient, scmCommentPostingEnabled: false },
      response: { ok: true },
    })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()
    vm.editedScmCommentPostingEnabled.value = false

    await vm.saveAdvancedSettings()

    // The pass list was not touched, so it is omitted from the patch — saving System-tab toggles must not
    // clobber a concurrently-edited (or still-loading) review-pass list.
    expect(mockPatch).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: {
        scmCommentPostingEnabled: false,
        enableEvidenceBackedVerification: false,
        enableMultiPassUnion: false,
        includeLinkedItemsInContext: true,
        enableLanguageRobustScreening: false,
      },
    })
  })

  it('sends reviewPasses only when the pass list changed, dropping empty entries', async () => {
    mockGet.mockReset()
    mockGet
      .mockResolvedValueOnce({ data: sampleClient, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleReviewProfiles, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleClientReviewProfile, response: { status: 200, ok: true } })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    // A newly chosen pass plus a half-configured (empty-model) row that must be dropped before sending.
    vm.editedReviewPasses.value = [
      { ordinal: 0, configuredModelId: 'model-x' },
      { ordinal: 1, configuredModelId: '' },
    ]

    await vm.saveAdvancedSettings()

    const body = mockPatch.mock.calls[0][1].body as Record<string, unknown>
    expect(body.reviewPasses).toEqual([{ ordinal: 0, configuredModelId: 'model-x', lens: null, scope: null, shadow: false }])
  })

  it('saves review aggressiveness through the dedicated review-profile endpoint', async () => {
    mockGet.mockReset()
    mockGet
      .mockResolvedValueOnce({ data: sampleClient, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleReviewProfiles, response: { status: 200, ok: true } })
      .mockResolvedValueOnce({ data: sampleClientReviewProfile, response: { status: 200, ok: true } })
    mockPut.mockResolvedValue({
      data: {
        clientId: 'client-1',
        defaultReviewPipelineProfileId: 'file-by-file-assertive',
        source: 'clientDefault',
        updatedAtUtc: '2026-05-31T12:00:00Z',
      },
      response: { ok: true },
    })

    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()
    vm.editedDefaultReviewPipelineProfileId.value = 'file-by-file-assertive'

    await vm.saveReviewProfile()

    expect(mockPut).toHaveBeenCalledWith('/admin/clients/{clientId}/review-profile', {
      params: { path: { clientId: 'client-1' } },
      body: { defaultReviewPipelineProfileId: 'file-by-file-assertive' },
    })
    expect(vm.clientReviewProfile.value?.source).toBe('clientDefault')
  })

  it('navigates back to clients when the detail record is not found', async () => {
    mockGet.mockReset()
    mockGet.mockResolvedValueOnce({ data: null, response: { status: 404, ok: false } })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    expect(vm.notFound.value).toBe(true)
    expect(mockRouterPush).toHaveBeenCalledWith({ name: 'clients' })
  })
})
