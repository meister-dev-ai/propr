// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { components } from '@/types'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: () => true,
  }),
}))

import { useReviewHistoryViewModel } from '@/features/reviews/view-models/useReviewHistoryViewModel'

type JobListItem = components['schemas']['JobListItem']

function completedItem(overrides: Partial<JobListItem> = {}): JobListItem {
  return {
    id: 'job-1',
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId: 42,
    iterationId: 1,
    status: 'completed',
    submittedAt: '2026-07-12T00:00:00Z',
    resultSummaryExcerpt: 'the opening excerpt',
    hasResultSummary: true,
    ...overrides,
  } as JobListItem
}

describe('useReviewHistoryViewModel summary modal', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows the excerpt immediately and replaces it with the full text', async () => {
    const getJobDetail = vi.fn().mockResolvedValue({ resultSummary: 'the complete summary text' })
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({ items: [completedItem()] }),
        getJobDetail,
      },
    })
    await vm.refresh()

    await vm.openSummaryModal(vm.groups.value[0].items[0])

    expect(vm.isSummaryModalOpen.value).toBe(true)
    expect(vm.selectedSummary.value).toBe('the complete summary text')
    expect(getJobDetail).toHaveBeenCalledWith('job-1')
    expect(vm.summaryLoading.value).toBe(false)
  })

  it('keeps the excerpt on screen when the full text cannot be fetched', async () => {
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({ items: [completedItem()] }),
        getJobDetail: vi.fn().mockRejectedValue(new Error('offline')),
      },
    })
    await vm.refresh()

    await vm.openSummaryModal(vm.groups.value[0].items[0])

    expect(vm.isSummaryModalOpen.value).toBe(true)
    expect(vm.selectedSummary.value).toBe('the opening excerpt')
    expect(vm.summaryLoading.value).toBe(false)
  })

  it('shows the failure message without fetching when there is no summary', async () => {
    const getJobDetail = vi.fn()
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({
          items: [completedItem({
            status: 'failed',
            hasResultSummary: false,
            resultSummaryExcerpt: null,
            errorMessage: 'the provider returned 429',
          })],
        }),
        getJobDetail,
      },
    })
    await vm.refresh()

    await vm.openSummaryModal(vm.groups.value[0].items[0])

    expect(vm.selectedSummary.value).toBe('the provider returned 429')
    expect(getJobDetail).not.toHaveBeenCalled()
  })

  it('does not open for a job still processing', async () => {
    const getJobDetail = vi.fn()
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({
          items: [completedItem({ status: 'processing', hasResultSummary: false, resultSummaryExcerpt: null })],
        }),
        getJobDetail,
      },
    })
    await vm.refresh()

    await vm.openSummaryModal(vm.groups.value[0].items[0])

    expect(vm.isSummaryModalOpen.value).toBe(false)
    expect(getJobDetail).not.toHaveBeenCalled()
  })
})
