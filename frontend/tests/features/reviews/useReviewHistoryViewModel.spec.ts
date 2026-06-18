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

  it('loads jobs, groups them, and exposes protocol chips for processing items', async () => {
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
    const getJobProtocol = vi.fn(async () => [{ id: 'protocol-1' }])

    const vm = await mountViewModel(() => useReviewHistoryViewModel({
      clientId: 'client-1',
      reviewHistoryService: { listJobs, getJobProtocol } as unknown as Partial<ReviewHistoryService>,
    }))

    expect(listJobs).toHaveBeenCalledWith('client-1')
    expect(vm.groups.value).toHaveLength(1)
    expect(vm.groups.value[0]?.items).toHaveLength(1)
    expect(vm.groups.value[0]?.prTitle).toBe('Improve runtime handling')
    expect(getJobProtocol).toHaveBeenCalledWith('job-1')
    expect(vm.processingProtocols.value['job-1']).toEqual([{ id: 'protocol-1' }])
  })

  it('opens the summary modal only when a non-processing item has content', async () => {
    const vm = await mountViewModel(() => useReviewHistoryViewModel({
      autoLoad: false,
      reviewHistoryService: {
        listJobs: async () => ({ items: [] }),
        getJobProtocol: async () => [],
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
})
