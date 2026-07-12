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

function processingItem(overrides: Partial<JobListItem> = {}): JobListItem {
  return {
    id: 'job-1',
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId: 42,
    iterationId: 7,
    status: 'processing',
    submittedAt: '2026-07-12T00:00:00Z',
    filesReviewed: 12,
    filesInScope: 40,
    ...overrides,
  } as JobListItem
}

describe('useReviewHistoryViewModel progress fields', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('preserves filesReviewed/filesInScope on grouped items so the row can render "X/Y"', async () => {
    const item = processingItem()
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: vi.fn().mockResolvedValue({ items: [item] }),
        getJobProtocol: vi.fn().mockResolvedValue([]),
        restartJob: vi.fn().mockResolvedValue(undefined),
      },
    })

    await vm.refresh()

    const group = vm.groups.value[0]
    expect(group).toBeDefined()
    const rows = vm.visibleItems(group)
    const row = rows.find(r => r.id === 'job-1')
    expect(row?.filesReviewed).toBe(12)
    expect(row?.filesInScope).toBe(40)
  })
})
