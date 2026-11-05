// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import type { components } from '@/types'

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: () => true,
  }),
}))

import { useReviewHistoryViewModel } from '@/features/reviews/view-models/useReviewHistoryViewModel'

type PullRequestHistoryResponse = components['schemas']['PullRequestHistoryResponse']

function page(pullRequestUrl: string | null): PullRequestHistoryResponse {
  return {
    total: 1,
    items: [
      {
        providerScopePath: 'http://localhost:8091',
        providerProjectKey: 'local_admin',
        repositoryId: '1',
        pullRequestId: 3,
        clientId: 'client-1',
        prTitle: 'A pull request',
        prRepositoryName: 'propr',
        pullRequestUrl,
        latestActivityAt: '2026-05-01T10:00:00Z',
        totalInputTokens: 0,
        totalOutputTokens: 0,
        jobs: [],
      },
    ],
  } as PullRequestHistoryResponse
}

describe('useReviewHistoryViewModel pull request address', () => {
  it('takes the address of the pull request page from the server', async () => {
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listPullRequestHistory: vi.fn().mockResolvedValue(page('http://localhost:8091/local_admin/propr/pulls/3')),
      },
    })

    await vm.refresh()

    expect(vm.groups.value[0]?.prUrl).toBe('http://localhost:8091/local_admin/propr/pulls/3')
  })

  it('has no address when the server built none', async () => {
    const vm = useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: { listPullRequestHistory: vi.fn().mockResolvedValue(page(null)) },
    })

    await vm.refresh()

    expect(vm.groups.value[0]?.prUrl).toBeNull()
  })
})
