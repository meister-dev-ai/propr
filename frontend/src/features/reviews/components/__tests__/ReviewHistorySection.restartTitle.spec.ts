// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { computed, ref } from 'vue'
import type { components } from '@/types'

type JobListItem = components['schemas']['JobListItem']

const groups = ref<unknown[]>([])

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
    // Every row in this file stands for a status the restart button is offered for, including one the
    // view-model does not accept today, so the neutral default is reachable.
    isRestartable: () => true,
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

function item(status: string): JobListItem {
  return {
    id: `job-${status}`,
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId: 42,
    iterationId: 1,
    status,
    submittedAt: '2026-07-15T10:00:00Z',
  } as unknown as JobListItem
}

/** Renders one pull request group holding a row per given status, and returns the restart button titles by status. */
function restartTitles(statuses: string[]): Record<string, string> {
  groups.value = [
    {
      key: 'group-1',
      pullRequestId: 42,
      providerScopePath: 'https://dev.azure.com/org',
      providerProjectKey: 'proj',
      repositoryId: 'repo-1',
      prTitle: 'A pull request',
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
      items: statuses.map(item),
    },
  ]

  const wrapper = mount(ReviewHistorySection, {
    global: {
      stubs: {
        ModalDialog: { template: '<div />' },
        OverflowMenu: { template: '<div />' },
        ProgressOrb: { template: '<div />' },
      },
    },
  })

  return Object.fromEntries(
    statuses.map((status, index) => [status, wrapper.findAll('.restart-btn')[index].attributes('title') ?? '']),
  )
}

describe('ReviewHistorySection restart button title', () => {
  it('names the budget and what frees it for a review a budget held or stopped', () => {
    const titles = restartTitles(['budgetHeld', 'budgetExceeded'])

    expect(titles.budgetHeld).toBe('Restart this held review after freeing budget')
    expect(titles.budgetExceeded).toBe('Restart this budget-stopped review after freeing budget')
  })

  it('names neither budget nor a remedy for a review that failed', () => {
    const titles = restartTitles(['failed'])

    expect(titles.failed).toContain('failed')
    expect(titles.failed).not.toContain('budget')
  })

  // A review limit is not a budget, and the row carries no refusal reason, so the title covers both ways out:
  // the measured value comes down, or the bound that refused it goes up. Splitting the pull request is not one
  // of them for the repository-size bound, which the repository keeps whatever the pull request contains.
  it('names both remedies, and no budget, for a refused review', () => {
    const titles = restartTitles(['admissionRefused'])

    expect(titles.admissionRefused).toBe(
      'Restart this review after reducing what the review limit measured, or raising that limit',
    )
  })

  it('claims nothing about the cause for any other status', () => {
    const titles = restartTitles(['cancelled'])

    expect(titles.cancelled).toBe('Restart this review')
  })
})
