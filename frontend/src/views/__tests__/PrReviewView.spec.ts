import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const routeQuery = ref<Record<string, string | undefined>>({})
const getPrViewMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')

  return {
    ...actual,
    useRoute: () => ({ query: routeQuery.value }),
  }
})

vi.mock('@/services/jobsService', () => ({
  getPrView: getPrViewMock,
}))

async function mountView() {
  const { default: PrReviewView } = await import('@/features/reviews/views/PrReviewView.vue')
  return mount(PrReviewView, {
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a :href="typeof to === \'string\' ? to : JSON.stringify(to)"><slot /></a>',
        },
        TokenBreakdownTable: {
          props: ['breakdown', 'breakdownConsistent'],
          template: '<div class="token-breakdown-table-stub">Token breakdown</div>',
        },
      },
    },
  })
}

describe('PrReviewView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    routeQuery.value = {}
  })

  it('shows the query guidance when required route params are missing', async () => {
    const wrapper = await mountView()
    await flushPromises()

    expect(getPrViewMock).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('No data. Provide clientId, providerScopePath, providerProjectKey, repositoryId and pullRequestId query parameters.')
  })

  it('loads the PR review summary for the selected client and pull request', async () => {
    routeQuery.value = {
      clientId: 'client-1',
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-a',
      repositoryId: 'repo-a',
      pullRequestId: '42',
    }
    getPrViewMock.mockResolvedValue({
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-a',
      repositoryId: 'repo-a',
      pullRequestId: 42,
      totalJobs: 2,
      totalInputTokens: 3200,
      totalOutputTokens: 900,
      aggregatedTokenBreakdown: [{ connectionCategory: 0, modelId: 'gpt-test', totalInputTokens: 3200, totalOutputTokens: 900 }],
      breakdownConsistent: true,
      jobs: [{ jobId: 'job-1', status: 2, submittedAt: '2026-04-25T10:00:00Z', completedAt: '2026-04-25T10:05:00Z', findingCount: 1, totalInputTokens: 1600, totalOutputTokens: 400, tokenBreakdown: [] }],
      originatedMemoryCount: 1,
      originatedMemories: [{ memoryRecordId: 'mem-1', threadId: 12, filePath: 'src/foo.ts', resolutionSummaryExcerpt: 'Resolved issue', source: 0, storedAt: '2026-04-25T10:06:00Z' }],
      contributedMemoryCount: 0,
      contributedMemories: [],
      totalEstimatedCostUsd: 1.234567,
      costIsApproximate: false,
    })

    const wrapper = await mountView()
    await flushPromises()

    expect(getPrViewMock).toHaveBeenCalledWith('client-1', {
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-a',
      repositoryId: 'repo-a',
      pullRequestId: 42,
    })
    expect(wrapper.text()).toContain('PR Review View')
    expect(wrapper.text()).toContain('PR #42')
    expect(wrapper.text()).toContain('Review Jobs')
    expect(wrapper.text()).toContain('Est. Cost')
    expect(wrapper.text()).toContain('$1.23')
  })

  it('surfaces the service error when the PR review request fails', async () => {
    routeQuery.value = {
      clientId: 'client-1',
      providerScopePath: 'https://dev.azure.com/example',
      providerProjectKey: 'project-a',
      repositoryId: 'repo-a',
      pullRequestId: '42',
    }
    getPrViewMock.mockRejectedValue(new Error('PR view unavailable'))

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.text()).toContain('PR view unavailable')
  })
})
