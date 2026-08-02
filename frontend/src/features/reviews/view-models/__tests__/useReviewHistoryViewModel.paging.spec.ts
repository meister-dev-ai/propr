// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { components } from '@/types'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: () => true,
  }),
}))

import { useReviewHistoryViewModel } from '@/features/reviews/view-models/useReviewHistoryViewModel'

type PullRequestHistoryResponse = components['schemas']['PullRequestHistoryResponse']

function group(pullRequestId: number, status = 'completed') {
  return {
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId,
    clientId: 'client-1',
    prTitle: `PR ${pullRequestId}`,
    prRepositoryName: 'repo-1',
    prSourceBranch: 'feature',
    prTargetBranch: 'main',
    latestActivityAt: '2026-05-01T10:00:00Z',
    totalInputTokens: 100,
    totalOutputTokens: 10,
    totalEstimatedCostUsd: 0.5,
    costIsApproximate: false,
    jobs: [{ id: `job-${pullRequestId}`, clientId: 'client-1', status, pullRequestId }],
  }
}

function page(pullRequestIds: number[], total: number, status = 'completed'): PullRequestHistoryResponse {
  return { total, items: pullRequestIds.map(id => group(id, status)) } as PullRequestHistoryResponse
}

describe('useReviewHistoryViewModel server paging', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('drives the pager from the total the server reports, not from what is loaded', async () => {
    const listPullRequestHistory = vi.fn().mockResolvedValue(page([1, 2, 3], 74))
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory },
    })

    await vm.refresh()

    // Three groups are loaded, but seventy-four exist: ten per page is eight pages.
    expect(vm.groups.value).toHaveLength(3)
    expect(vm.totalGroups.value).toBe(74)
    expect(vm.totalPages.value).toBe(8)
  })

  it('fetches the next page rather than slicing what is already loaded', async () => {
    const listPullRequestHistory = vi.fn().mockResolvedValue(page([1], 50))
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory },
    })
    await vm.refresh()

    await vm.nextPage()

    expect(vm.currentPage.value).toBe(2)
    expect(listPullRequestHistory).toHaveBeenLastCalledWith(
      expect.objectContaining({ limit: 10, offset: 10 }))
  })

  it('will not page past the end', async () => {
    const listPullRequestHistory = vi.fn().mockResolvedValue(page([1], 4))
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory },
    })
    await vm.refresh()
    listPullRequestHistory.mockClear()

    await vm.nextPage()

    expect(vm.currentPage.value).toBe(1)
    expect(listPullRequestHistory).not.toHaveBeenCalled()
  })

  it('reaches history the old five-hundred-job ceiling made unreachable', async () => {
    const listPullRequestHistory = vi.fn().mockResolvedValue(page([9001], 7958))
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory },
    })
    await vm.refresh()

    vm.currentPage.value = 700
    await vm.nextPage()

    expect(listPullRequestHistory).toHaveBeenLastCalledWith(
      expect.objectContaining({ offset: 7000 }))
  })
})

describe('useReviewHistoryViewModel poll guard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.useFakeTimers()
  })

  // Restored unconditionally: a test that fails partway would otherwise leave fake timers installed
  // for whatever this worker runs next, which surfaces as unrelated suites timing out.
  afterEach(() => {
    vi.useRealTimers()
  })

  it('skips a poll tick while a load is still outstanding', async () => {
    let release: (value: PullRequestHistoryResponse) => void = () => {}
    const first = new Promise<PullRequestHistoryResponse>((resolve) => { release = resolve })

    const listPullRequestHistory = vi.fn()
      .mockReturnValueOnce(first)
      .mockResolvedValue(page([1], 1, 'processing'))

    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory },
    })

    // Start a load and leave it outstanding, then let several poll intervals elapse.
    const pending = vm.refresh()
    await vi.advanceTimersByTimeAsync(9000)

    expect(listPullRequestHistory).toHaveBeenCalledTimes(1)

    release(page([1], 1, 'processing'))
    await pending
  })

  it('polls again once the outstanding load has settled', async () => {
    const listPullRequestHistory = vi.fn().mockResolvedValue(page([1], 1, 'processing'))
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory },
    })

    await vm.refresh()
    expect(listPullRequestHistory).toHaveBeenCalledTimes(1)

    // A running review keeps the poll alive, and with nothing outstanding each tick fetches.
    await vi.advanceTimersByTimeAsync(3000)
    expect(listPullRequestHistory).toHaveBeenCalledTimes(2)
  })
})
