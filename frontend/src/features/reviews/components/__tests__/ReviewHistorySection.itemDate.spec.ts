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

const submittedAt = '2026-07-15T10:00:00Z'
const completedAt = '2026-07-15T11:30:00Z'

function item(status: string, overrides: Partial<JobListItem> = {}): JobListItem {
  return {
    id: `job-${status}`,
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/org',
    providerProjectKey: 'proj',
    repositoryId: 'repo-1',
    pullRequestId: 42,
    iterationId: 1,
    status,
    submittedAt,
    ...overrides,
  } as unknown as JobListItem
}

/** Renders one pull request group holding the given rows, and returns the date cell text of each in order. */
function dateCells(items: JobListItem[]): string[] {
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
      latestActivityAt: submittedAt,
      totalInTokens: 0,
      totalOutTokens: 0,
      totalEstimatedCostUsd: null,
      costIsApproximate: false,
      clientId: 'client-1',
      items,
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

  return wrapper.findAll('.date-cell').map((cell) => cell.text())
}

describe('ReviewHistorySection row date', () => {
  // A held review has not completed, so the completion time every other row carries is absent on it. It has
  // been accepted and waits for its hour to pass, the same state a pending row reports.
  it('dates a held review from when it was submitted, as it dates a pending one', () => {
    const [held, pending] = dateCells([item('admissionHeld'), item('pending')])

    expect(held).toBe(`Queued ${new Date(submittedAt).toLocaleString()}`)
    expect(held).toBe(pending)
  })

  it('dates a completed review from when it completed', () => {
    const [completed] = dateCells([item('completed', { completedAt } as Partial<JobListItem>)])

    expect(completed).toBe(new Date(completedAt).toLocaleString())
  })
})
