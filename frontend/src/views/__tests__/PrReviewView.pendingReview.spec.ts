// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const routeQuery = ref<Record<string, string | undefined>>({})
const getPrViewMock = vi.fn()
const listBlockedPrsMock = vi.fn()
const submitReviewByCoordinatesMock = vi.fn()
// Null stands for "holds no role on this client at all", which zero cannot express: zero is the User role.
let assignedRole: number | null = 1

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
  return {
    ...actual,
    useRoute: () => ({ query: routeQuery.value }),
  }
})

vi.mock('@/services/jobsService', () => ({
  getPrView: (...args: unknown[]) => getPrViewMock(...args),
  listBlockedPrs: (...args: unknown[]) => listBlockedPrsMock(...args),
  blockPr: vi.fn(),
  unblockPr: vi.fn(),
  restartJob: vi.fn(),
  submitReviewByCoordinates: (...args: unknown[]) => submitReviewByCoordinatesMock(...args),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: (clientId: string, minRole: number) =>
      clientId === 'client-1' && assignedRole !== null && assignedRole >= minRole,
    isCapabilityAvailable: () => false,
  }),
}))

vi.mock('@/features/reviews/composables/useRetainedPrData', () => ({
  useRetainedPrData: () => ({ load: vi.fn().mockResolvedValue(undefined) }),
}))

const identityQuery = {
  clientId: 'client-1',
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: 'repo-a',
  pullRequestId: '42',
}

const expectedIdentity = {
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: 'repo-a',
  pullRequestId: 42,
}

function viewDto(pendingReview: unknown) {
  return {
    ...expectedIdentity,
    totalJobs: 0,
    totalInputTokens: 0,
    totalOutputTokens: 0,
    aggregatedTokenBreakdown: [],
    breakdownConsistent: true,
    jobs: [],
    originatedMemoryCount: 0,
    originatedMemories: [],
    contributedMemoryCount: 0,
    contributedMemories: [],
    pendingReview,
  }
}

async function mountView() {
  const { default: PrReviewView } = await import('@/features/reviews/views/PrReviewView.vue')
  return mount(PrReviewView, {
    global: {
      stubs: {
        RouterLink: { props: ['to'], template: '<a><slot /></a>' },
        TokenBreakdownTable: { template: '<div />' },
        RetainedConversationTab: { template: '<div />' },
        RetainedBrowserTab: { template: '<div />' },
      },
    },
  })
}

describe('PrReviewView — a pull request waiting for a review', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    assignedRole = 1
    routeQuery.value = { ...identityQuery }
    listBlockedPrsMock.mockResolvedValue([])
    getPrViewMock.mockResolvedValue(viewDto(null))
  })

  it('says nothing when the pull request is not waiting', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('.pending-review').exists()).toBe(false)
  })

  it('offers the review, and says what is waiting and since when', async () => {
    getPrViewMock.mockResolvedValue(
      viewDto({
        revisionKey: 'iter-3',
        reviewedRevisionKey: 'iter-1',
        detectedAt: '2026-08-04T09:00:00Z',
      }),
    )

    const wrapper = await mountView()
    await flushPromises()

    const banner = wrapper.find('.pending-review')
    expect(banner.exists()).toBe(true)
    expect(banner.text()).toContain('New commits since the last review')
    expect(banner.text()).toContain('iter-1')
    expect(banner.find('.pending-review__action').text()).toContain('Review current state')
  })

  it('says the files were never reviewed rather than naming a revision it has none for', async () => {
    getPrViewMock.mockResolvedValue(
      viewDto({ revisionKey: 'iter-3', reviewedRevisionKey: null, detectedAt: null }),
    )

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('.pending-review').text()).toContain('have not been reviewed yet')
  })

  it('requests the review by coordinates and reloads once it is queued', async () => {
    getPrViewMock.mockResolvedValue(
      viewDto({ revisionKey: 'iter-3', reviewedRevisionKey: 'iter-1', detectedAt: null }),
    )
    submitReviewByCoordinatesMock.mockResolvedValue({ outcome: 'submitted', jobId: 'job-1' })

    const wrapper = await mountView()
    await flushPromises()
    getPrViewMock.mockClear()

    await wrapper.find('.pending-review__action').trigger('click')
    await flushPromises()

    expect(submitReviewByCoordinatesMock).toHaveBeenCalledWith('client-1', expectedIdentity)
    expect(getPrViewMock).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.pending-review__result').text()).toContain('Review requested')
  })

  /**
   * A refusal is an answer, not a failure. Reloading over it would replace the explanation with an
   * unchanged page and leave the person unsure whether anything happened.
   */
  it('explains a refusal without reloading', async () => {
    getPrViewMock.mockResolvedValue(
      viewDto({ revisionKey: 'iter-3', reviewedRevisionKey: 'iter-1', detectedAt: null }),
    )
    submitReviewByCoordinatesMock.mockResolvedValue({ outcome: 'duplicateActiveJob' })

    const wrapper = await mountView()
    await flushPromises()
    getPrViewMock.mockClear()

    await wrapper.find('.pending-review__action').trigger('click')
    await flushPromises()

    expect(getPrViewMock).not.toHaveBeenCalled()
    expect(wrapper.find('.pending-review__result').text()).toContain('already running')
  })

  it('tells someone who may not request a review that the pull request is waiting anyway', async () => {
    assignedRole = null
    getPrViewMock.mockResolvedValue(
      viewDto({ revisionKey: 'iter-3', reviewedRevisionKey: 'iter-1', detectedAt: null }),
    )

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('.pending-review').exists()).toBe(true)
    expect(wrapper.find('.pending-review__action').exists()).toBe(false)
  })
})
