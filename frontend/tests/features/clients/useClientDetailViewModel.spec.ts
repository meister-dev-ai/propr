// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent } from 'vue'

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
  enableMultiPassUnion: false,
  reviewEveryIncrementEnabled: false,
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
        enableMultiPassUnion: false,
        reviewEveryIncrementEnabled: false,
        commentResolutionBehavior: 'silent',
        includeLinkedItemsInContext: true,
        outputLanguage: 'en',
        baselineReasoningEffort: 'none',
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
    expect(body.reviewPasses).toEqual([
      { ordinal: 0, configuredModelId: 'model-x', logicalModelName: null, lens: null, scope: null, shadow: false, reasoningEffort: 'none' },
    ])
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

  it('surfaces a load error (not "not found") for a non-404 error response and stays on the page', async () => {
    mockGet.mockReset()
    mockRouterPush.mockClear()
    mockGet.mockResolvedValueOnce({ data: undefined, response: { status: 500, ok: false } })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    expect(vm.loadError.value).toBe(true)
    expect(vm.notFound.value).toBe(false)
    expect(mockRouterPush).not.toHaveBeenCalledWith({ name: 'clients' })
  })

  // While ProPR stops the work of the client, the deletion answers 409 with the seconds to wait. The page shows the
  // wait, requests the deletion again after it and returns to the client list once the client is deleted.
  it('repeats the deletion after the wait that a 409 answer names and then returns to the client list', async () => {
    vi.useFakeTimers()
    try {
      mockDelete
        .mockResolvedValueOnce({
          error: { error: 'client_work_running', retryAfterSeconds: 60 },
          response: { status: 409, headers: new Headers({ 'Retry-After': '60' }) },
        })
        .mockResolvedValueOnce({ response: { status: 204, headers: new Headers() } })
      const vm = useClientDetailViewModel({ autoLoad: false })
      vm.showDeleteDialog.value = true

      await vm.handleDelete()

      expect(vm.showDeleteDialog.value).toBe(false)
      expect(vm.deletionWaitSeconds.value).toBe(60)
      expect(mockRouterPush).not.toHaveBeenCalled()

      await vi.advanceTimersByTimeAsync(60_000)

      expect(mockDelete).toHaveBeenCalledTimes(2)
      expect(vm.deletionWaitSeconds.value).toBeNull()
      expect(mockRouterPush).toHaveBeenCalledWith({ name: 'clients' })
    } finally {
      vi.useRealTimers()
    }
  })

  it('starts no further deletion request and does not navigate once the page was closed', async () => {
    vi.useFakeTimers()
    try {
      let answer!: (value: unknown) => void
      mockDelete.mockReturnValueOnce(new Promise((resolve) => { answer = resolve }))
      let vm!: ReturnType<typeof useClientDetailViewModel>
      const wrapper = mount(defineComponent({
        setup() {
          vm = useClientDetailViewModel({ autoLoad: false })
          return () => null
        },
      }))

      const deletion = vm.handleDelete()
      wrapper.unmount()
      answer({
        error: { error: 'client_work_running', retryAfterSeconds: 60 },
        response: { status: 409, headers: new Headers({ 'Retry-After': '60' }) },
      })
      await deletion
      await vi.advanceTimersByTimeAsync(120_000)

      expect(mockDelete).toHaveBeenCalledTimes(1)
      expect(mockRouterPush).not.toHaveBeenCalled()
    } finally {
      vi.useRealTimers()
    }
  })

  it('stays on the page and reports a deletion that failed', async () => {
    mockDelete.mockResolvedValueOnce({ error: {}, response: { status: 500, headers: new Headers() } })
    const vm = useClientDetailViewModel({ autoLoad: false })

    await vm.handleDelete()

    expect(vm.deleteError.value).toContain('could not be deleted')
    expect(vm.deletionWaitSeconds.value).toBeNull()
    expect(mockRouterPush).not.toHaveBeenCalled()
  })

  it('surfaces a load error when the request throws and does not redirect', async () => {
    mockGet.mockReset()
    mockRouterPush.mockClear()
    mockGet.mockRejectedValueOnce(new Error('network down'))
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    expect(vm.loadError.value).toBe(true)
    expect(vm.notFound.value).toBe(false)
    expect(mockRouterPush).not.toHaveBeenCalledWith({ name: 'clients' })
  })

  it('defaults omitted badge style to images and saves a plain badge choice', async () => {
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()
    expect(vm.editedFindingBadgeStyle.value).toBe('shieldsIo')
    expect(vm.isPostConfigButtonEnabled()).toBe(false)
    vm.editedFindingBadgeStyle.value = 'plain'
    expect(vm.isPostConfigButtonEnabled()).toBe(true)
    mockPatch.mockResolvedValue({ data: { ...sampleClient, findingBadgeStyle: 'plain' }, response: { ok: true } })
    await vm.savePostConfiguration()
    expect(mockPatch).toHaveBeenCalledWith('/clients/{clientId}', expect.objectContaining({
      body: expect.objectContaining({ findingBadgeStyle: 'plain' }),
    }))
    expect(vm.editedFindingBadgeStyle.value).toBe('plain')
    expect(vm.isPostConfigButtonEnabled()).toBe(false)
    mockGet.mockResolvedValueOnce({ data: { ...sampleClient, findingBadgeStyle: 'plain' } })
    await vm.loadClient()
    expect(vm.editedFindingBadgeStyle.value).toBe('plain')
  })

  it('sends the whole post configuration, out-of-scope withholding included', async () => {
    mockPatch.mockResolvedValue({
      data: { ...sampleClient, withholdOutOfScopeFindings: true },
      response: { ok: true },
    })
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()
    vm.editedWithholdOutOfScopeFindings.value = true

    await vm.savePostConfiguration()

    // The three controls share one Save, so the body carries all of them. A field missing here is a setting
    // the user can tick and never persist.
    expect(mockPatch).toHaveBeenCalledWith('/clients/{clientId}', {
      params: { path: { clientId: 'client-1' } },
      body: {
        findingBadgeStyle: 'shieldsIo',
        minimumSeverityToPost: 'info',
        autoResolveSeverities: [],
        withholdOutOfScopeFindings: true,
      },
    })
  })

  it('activates the post-configuration Save when only out-of-scope withholding changed', async () => {
    const vm = useClientDetailViewModel({ autoLoad: false })
    await vm.loadClient()

    // The loaded client carries no such field, which is what a client saved before the setting existed looks
    // like. Hydration has to read that as off, or every one of them would show the box ticked.
    expect(vm.editedWithholdOutOfScopeFindings.value).toBe(false)
    expect(vm.isPostConfigButtonEnabled()).toBe(false)

    vm.editedWithholdOutOfScopeFindings.value = true

    expect(vm.isPostConfigButtonEnabled()).toBe(true)
  })
})
