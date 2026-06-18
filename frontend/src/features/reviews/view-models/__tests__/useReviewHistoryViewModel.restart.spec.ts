// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises } from '@vue/test-utils'
import type { components } from '@/types'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: () => true,
  }),
}))

import { useReviewHistoryViewModel } from '@/features/reviews/view-models/useReviewHistoryViewModel'

type JobListItem = components['schemas']['JobListItem']

function failedItem(overrides: Partial<JobListItem> = {}): JobListItem {
  return {
    id: 'job-1',
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId: 42,
    iterationId: 7,
    status: 'failed',
    submittedAt: '2026-06-18T00:00:00Z',
    errorMessage: 'boom',
    ...overrides,
  } as JobListItem
}

describe('useReviewHistoryViewModel restart action', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('calls restart for a failed job and reloads', async () => {
    const item = failedItem()
    const restartJob = vi.fn().mockResolvedValue(undefined)
    const listJobs = vi.fn().mockResolvedValue({ items: [item] })

    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs,
        getJobProtocol: vi.fn().mockResolvedValue([]),
        restartJob,
      },
    })

    await vm.refresh()
    expect(listJobs).toHaveBeenCalledTimes(1)

    await vm.restartJob(item)
    await flushPromises()

    expect(restartJob).toHaveBeenCalledWith('job-1')
    expect(listJobs).toHaveBeenCalledTimes(2) // reloaded after restart
    expect(vm.restartingJobs.value.has('job-1')).toBe(false)
    expect(vm.restartError.value).toBe('')
  })

  it('does not restart a non-failed job', async () => {
    const restartJob = vi.fn().mockResolvedValue(undefined)
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({ items: [] }),
        getJobProtocol: vi.fn().mockResolvedValue([]),
        restartJob,
      },
    })

    await vm.restartJob(failedItem({ status: 'completed' }))

    expect(restartJob).not.toHaveBeenCalled()
  })

  it('surfaces an error message when restart fails', async () => {
    const item = failedItem()
    const restartJob = vi.fn().mockRejectedValue(new Error('Only failed review jobs can be restarted.'))
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({ items: [item] }),
        getJobProtocol: vi.fn().mockResolvedValue([]),
        restartJob,
      },
    })

    await vm.restartJob(item)
    await flushPromises()

    expect(vm.restartError.value).toBe('Only failed review jobs can be restarted.')
    expect(vm.restartingJobs.value.has('job-1')).toBe(false)
  })
})
