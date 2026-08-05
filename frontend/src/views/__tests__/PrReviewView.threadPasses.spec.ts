// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const routeQuery = ref<Record<string, string | undefined>>({})
const getPrViewMock = vi.fn()
const restartJobMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
  return {
    ...actual,
    useRoute: () => ({ query: routeQuery.value }),
  }
})

vi.mock('@/services/jobsService', () => ({
  getPrView: (...args: unknown[]) => getPrViewMock(...args),
  listBlockedPrs: vi.fn().mockResolvedValue([]),
  blockPr: vi.fn(),
  unblockPr: vi.fn(),
  restartJob: (...args: unknown[]) => restartJobMock(...args),
}))

vi.mock('@/features/reviews/composables/useRetainedPrData', () => ({
  useRetainedPrData: () => ({ load: vi.fn().mockResolvedValue(undefined) }),
}))

const identityQuery = {
  clientId: 'client-1',
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: 'repo-a',
  pullRequestId: '42',
}

function prView(threadPasses: unknown[], threadPassTotalEstimatedCostUsd: number | null) {
  return {
    providerScopePath: 'https://dev.azure.com/example',
    providerProjectKey: 'project-a',
    repositoryId: 'repo-a',
    pullRequestId: 42,
    totalJobs: 0,
    totalInputTokens: 0,
    totalOutputTokens: 0,
    aggregatedTokenBreakdown: [],
    breakdownConsistent: true,
    jobs: [],
    originatedMemoryCount: 0,
    originatedMemories: [],
    contributedMemoryCount: 0,
    contributedMemories: [],
    threadPasses,
    threadPassTotalEstimatedCostUsd,
    threadPassCostIsApproximate: false,
  }
}

async function mountView() {
  const { default: PrReviewView } = await import('@/features/reviews/views/PrReviewView.vue')
  return mount(PrReviewView, {
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a :href="typeof to === \'string\' ? to : JSON.stringify(to)"><slot /></a>',
        },
        TokenBreakdownTable: { template: '<div />' },
        RetainedConversationTab: { template: '<div />' },
        RetainedBrowserTab: { template: '<div />' },
      },
    },
  })
}

describe('PrReviewView thread passes', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    routeQuery.value = { ...identityQuery }
    restartJobMock.mockResolvedValue({ jobId: 'pass-1', sourceJobId: 'pass-1', status: 'pending' })
  })

  it('lists what each thread pass handled and spent, and links to its trace', async () => {
    getPrViewMock.mockResolvedValue(prView(
      [
        {
          threadPassId: 'pass-1',
          status: 2,
          createdAt: '2026-08-03T10:00:00Z',
          completedAt: '2026-08-03T10:01:00Z',
          threadCount: 3,
          totalInputTokens: 2400,
          totalOutputTokens: 300,
          totalEstimatedCostUsd: 0.75,
          costIsApproximate: false,
        },
      ],
      0.75,
    ))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Thread Passes')
    expect(wrapper.text()).toContain('3 thread(s)')
    expect(wrapper.text()).toContain('$0.75')
    expect(wrapper.html()).toContain('/jobs/pass-1/protocol?clientId=client-1')
  })

  it('offers a restart for a budget-held pass and reloads once it is queued again', async () => {
    getPrViewMock.mockResolvedValue(prView(
      [
        {
          threadPassId: 'pass-1',
          status: 5,
          createdAt: '2026-08-03T10:00:00Z',
          completedAt: null,
          threadCount: 0,
          totalInputTokens: 0,
          totalOutputTokens: 0,
          totalEstimatedCostUsd: null,
          costIsApproximate: false,
          budgetBlockScope: 2,
          budgetBlockCapKind: 0,
          budgetBlockThresholdUsd: 5,
          budgetBlockSpentUsd: 6,
        },
      ],
      null,
    ))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Budget held')
    expect(wrapper.text()).toContain('Restart it after freeing budget.')

    const restart = wrapper.findAll('button').find(button => button.text().startsWith('Restart'))
    expect(restart).toBeDefined()
    await restart!.trigger('click')
    await flushPromises()

    expect(restartJobMock).toHaveBeenCalledWith('pass-1')
    expect(getPrViewMock).toHaveBeenCalledTimes(2)
  })

  it('names a pass that ended having done nothing, and offers no restart for it', async () => {
    getPrViewMock.mockResolvedValue(prView(
      [
        {
          threadPassId: 'pass-1',
          status: 7,
          createdAt: '2026-08-03T10:00:00Z',
          completedAt: '2026-08-03T10:00:01Z',
          threadCount: 0,
          totalInputTokens: 0,
          totalOutputTokens: 0,
          totalEstimatedCostUsd: null,
          costIsApproximate: false,
          errorMessage: 'Thread interaction was switched off.',
        },
      ],
      null,
    ))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('Nothing to do')
    expect(wrapper.findAll('button').find(button => button.text().startsWith('Restart'))).toBeUndefined()
  })

  it('says so when no thread pass has run', async () => {
    getPrViewMock.mockResolvedValue(prView([], null))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('No thread passes have run for this PR.')
  })
})
