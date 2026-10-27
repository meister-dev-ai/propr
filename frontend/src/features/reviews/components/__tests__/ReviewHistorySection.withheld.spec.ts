// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import type { components } from '@/types'
import type { PrGroup } from '@/features/reviews/view-models/useReviewHistoryViewModel'

type JobListItem = components['schemas']['JobListItem']

const groups = ref<PrGroup[]>([])
const openSummaryModal = vi.fn()

vi.mock('vue-router', () => ({
  RouterLink: { props: ['to'], template: '<a class="router-link"><slot /></a>' },
}))

vi.mock('@/features/reviews/view-models/useReviewHistoryViewModel', () => ({
  useReviewHistoryViewModel: () => ({
    name: 'useReviewHistoryViewModel',
    loading: ref(false),
    error: ref(''),
    groups,
    expandedGroups: ref(new Set<string>()),
    currentPage: ref(1),
    totalGroups: ref(1),
    isSummaryModalOpen: ref(false),
    selectedSummary: ref(''),
    summaryLoading: ref(false),
    itemsVisibleDefault: 10,
    totalPages: computed(() => 1),
    paginatedGroups: computed(() => groups.value),
    openSummaryModal,
    toggleGroupExpanded: vi.fn(),
    nextPage: vi.fn(),
    previousPage: vi.fn(),
    refresh: vi.fn(),
    visibleItems: (group: { items: JobListItem[] }) => group.items,
    canInspectClient: () => true,
    canManageClient: () => false,
    isRestartable: () => false,
    restartingJobs: ref(new Set<string>()),
    restartError: ref(''),
    restartJob: vi.fn(),
    stoppingJobs: ref(new Set<string>()),
    stopError: ref(''),
    stopJob: vi.fn(),
    blockingPrs: ref(new Set<string>()),
    blockError: ref(''),
    isPrBlocked: () => false,
    toggleBlockPr: vi.fn(),
  }),
}))

import ReviewHistorySection from '@/features/reviews/components/ReviewHistorySection.vue'

function render(contentWithheld: boolean) {
  const job: JobListItem = {
    id: 'job-1',
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId: 42,
    iterationId: 1,
    status: 'completed',
    submittedAt: '2026-07-15T10:00:00Z',
    completedAt: '2026-07-15T11:00:00Z',
    hasResultSummary: true,
    resultSummaryExcerpt: contentWithheld ? null : 'Two findings.',
    contentWithheld,
  }
  groups.value = [
    {
      key: 'group-1',
      pullRequestId: 42,
      providerScopePath: 'https://dev.azure.com/org',
      providerProjectKey: 'proj',
      repositoryId: 'repo-1',
      prTitle: contentWithheld ? null : 'A pull request',
      prRepositoryName: 'repo',
      prSourceBranch: 'feature',
      prTargetBranch: 'main',
      prUrl: 'https://example.invalid/pr/42',
      latestActivityAt: '2026-07-15T10:00:00Z',
      totalInTokens: 0,
      totalOutTokens: 0,
      totalEstimatedCostUsd: null,
      costIsApproximate: false,
      clientId: 'client-1',
      contentWithheld,
      items: [job],
    },
  ]

  return mount(ReviewHistorySection, {
    global: {
      stubs: {
        ModalDialog: { template: '<div />' },
        OverflowMenu: { template: '<div />' },
        ProgressOrb: { template: '<div />' },
      },
    },
  })
}

describe('ReviewHistorySection for a platform administrator without an elevation', () => {
  it('names the title and the summary as withheld and opens no summary', async () => {
    const wrapper = render(true)

    expect(wrapper.get('[data-testid="pr-title-withheld"]').text()).toBe('Title withheld')
    expect(wrapper.get('[data-testid="summary-withheld"]').text()).toContain('Withheld')
    await wrapper.get('[data-testid="summary-withheld"]').trigger('click')
    expect(openSummaryModal).not.toHaveBeenCalled()
  })

  it('shows the title and the summary where the content is readable', () => {
    const wrapper = render(false)

    expect(wrapper.find('[data-testid="pr-title-withheld"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('A pull request')
    expect(wrapper.text()).toContain('Two findings.')
  })
})
