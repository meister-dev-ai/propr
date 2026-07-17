// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, describe, expect, it, vi } from 'vitest'
import type { ReviewHistoryService } from '@/features/reviews/view-models/useReviewHistoryViewModel'
import { flushPromises } from '@vue/test-utils'
import { createApp, defineComponent } from 'vue'
import { useReviewHistoryViewModel } from '@/features/reviews/view-models/useReviewHistoryViewModel'

async function mountViewModel(
  factory: () => ReturnType<typeof useReviewHistoryViewModel>,
): Promise<ReturnType<typeof useReviewHistoryViewModel>> {
  let vm: ReturnType<typeof useReviewHistoryViewModel> | null = null
  const app = createApp(defineComponent({
    setup() {
      vm = factory()
      return () => null
    },
  }))

  app.mount(document.createElement('div'))
  await flushPromises()

  if (!vm) {
    throw new Error('Failed to mount review history view model.')
  }

  return vm
}

describe('useReviewHistoryViewModel', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('loads and groups processing items from the list projection alone (no per-job protocol fetch)', async () => {
    const listJobs = vi.fn(async () => ({
      items: [
        {
          id: 'job-1',
          clientId: 'client-1',
          providerScopePath: 'https://dev.azure.com/acme',
          providerProjectKey: 'proj',
          repositoryId: 'repo-a',
          pullRequestId: 42,
          iterationId: 2,
          status: 'processing',
          submittedAt: '2026-05-01T10:00:00Z',
          processingStartedAt: '2026-05-01T10:01:00Z',
          completedAt: null,
          resultSummary: null,
          errorMessage: null,
          totalInputTokens: 50,
          totalOutputTokens: 10,
          prTitle: 'Improve runtime handling',
          prRepositoryName: 'repo-a',
          prSourceBranch: 'feature/runtime',
          prTargetBranch: 'main',
        },
      ],
    }))

    const vm = await mountViewModel(() => useReviewHistoryViewModel({
      clientId: 'client-1',
      reviewHistoryService: { listJobs } as unknown as Partial<ReviewHistoryService>,
    }))

    expect(listJobs).toHaveBeenCalledWith('client-1')
    expect(vm.groups.value).toHaveLength(1)
    expect(vm.groups.value[0]?.items).toHaveLength(1)
    expect(vm.groups.value[0]?.items[0]?.status).toBe('processing')
    expect(vm.groups.value[0]?.prTitle).toBe('Improve runtime handling')
    // The heavy per-job protocol fetch has been removed from polling; the chip is driven by the
    // list projection, so the view model no longer exposes a processingProtocols cache at all.
    expect('processingProtocols' in vm).toBe(false)
  })

  it('opens the summary modal only for a non-processing item with content', async () => {
    const vm = await mountViewModel(() => useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: async () => ({ items: [] }),
      },
    }))

    vm.openSummaryModal({
      id: 'job-2',
      status: 'completed',
      resultSummary: 'Summary text',
      errorMessage: null,
    } as never)

    expect(vm.isSummaryModalOpen.value).toBe(true)
    expect(vm.selectedSummary.value).toBe('Summary text')
  })

  it('never opens the summary modal for a processing item', async () => {
    const vm = await mountViewModel(() => useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: async () => ({ items: [] }),
      },
    }))

    vm.openSummaryModal({
      id: 'job-3',
      status: 'processing',
      resultSummary: null,
      errorMessage: 'transient',
    } as never)

    expect(vm.isSummaryModalOpen.value).toBe(false)
  })
})
