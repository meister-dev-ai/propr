// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import type { components } from '@/types'
import type { PrGroup } from '@/features/reviews/view-models/useReviewHistoryViewModel'

type JobListItem = components['schemas']['JobListItem']

const groups = ref<PrGroup[]>([])

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
    openSummaryModal: vi.fn(),
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

function render(prUrl: string | null) {
  groups.value = [
    {
      key: 'group-1',
      pullRequestId: 3,
      providerScopePath: 'http://localhost:8091',
      providerProjectKey: 'local_admin',
      repositoryId: '1',
      prTitle: 'A pull request',
      prRepositoryName: 'propr',
      prSourceBranch: 'feature',
      prTargetBranch: 'main',
      prUrl,
      latestActivityAt: '2026-07-15T10:00:00Z',
      totalInTokens: 0,
      totalOutTokens: 0,
      totalEstimatedCostUsd: null,
      costIsApproximate: false,
      clientId: 'client-1',
      contentWithheld: false,
      items: [],
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

describe('ReviewHistorySection pull request title', () => {
  it('links the title to the address the server built', () => {
    const wrapper = render('http://localhost:8091/local_admin/propr/pulls/3')

    const link = wrapper.get('[data-testid="pr-title-link"]')
    expect(link.attributes('href')).toBe('http://localhost:8091/local_admin/propr/pulls/3')
    expect(link.text()).toBe('A pull request')
  })

  it('shows the title without a link when the server built no address', () => {
    const wrapper = render(null)

    expect(wrapper.find('[data-testid="pr-title-link"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="pr-title-text"]').text()).toBe('A pull request')
  })
})
