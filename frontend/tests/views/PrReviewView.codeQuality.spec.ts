// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { ref } from 'vue'

const routeQuery = ref<Record<string, string | undefined>>({})

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
  return { ...actual, useRoute: () => ({ query: routeQuery.value }) }
})

const getPrViewMock = vi.fn()
vi.mock('@/services/jobsService', () => ({
  getPrView: getPrViewMock,
  listBlockedPrs: vi.fn().mockResolvedValue([]),
  blockPr: vi.fn(),
  unblockPr: vi.fn(),
}))

// The tab's contents are the code-quality workspace, tested on its own. Here the question is only whether the tab
// is offered on the one-pull-request view, and with which scope.
vi.mock('@/features/reviews/components/PrCodeQualityTab.vue', () => ({
  default: {
    name: 'PrCodeQualityTab',
    props: ['clientId', 'repositoryId', 'pullRequestId'],
    template:
      '<div class="pr-code-quality-stub">{{ clientId }}|{{ repositoryId }}|{{ pullRequestId }}</div>',
  },
}))

const capabilityAvailable = vi.fn()
vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    isCapabilityAvailable: (key: string) => capabilityAvailable(key),
    hasClientRole: () => true,
  }),
}))

const PR_QUERY = {
  clientId: 'client-1',
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: '4',
  pullRequestId: '4821',
}

const PR_VIEW = {
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: '4',
  pullRequestId: 4821,
  totalJobs: 3,
  totalInputTokens: 3200,
  totalOutputTokens: 900,
  aggregatedTokenBreakdown: [],
  breakdownConsistent: true,
  jobs: [],
  originatedMemoryCount: 0,
  originatedMemories: [],
  contributedMemoryCount: 0,
  contributedMemories: [],
  totalEstimatedCostUsd: null,
  costIsApproximate: false,
}

async function mountView() {
  const { default: PrReviewView } = await import('@/features/reviews/views/PrReviewView.vue')
  const wrapper = mount(PrReviewView, {
    global: {
      stubs: {
        RouterLink: { template: '<a><slot /></a>' },
        TokenBreakdownTable: { template: '<div />' },
        OverflowMenu: { template: '<div><slot :close="() => {}" /></div>' },
        RetainedConversationTab: { template: '<div />' },
        RetainedBrowserTab: { template: '<div />' },
      },
    },
  })
  await flushPromises()
  return wrapper
}

describe('PrReviewView. Code Quality tab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    routeQuery.value = { ...PR_QUERY }
    capabilityAvailable.mockReturnValue(true)
    getPrViewMock.mockResolvedValue(PR_VIEW)
  })

  it('offers the tab on the view that shows one pull request', async () => {
    // This is the surface that spans every review of the pull request, which is the scope the collected numbers
    // are gathered at.
    const wrapper = await mountView()

    expect(wrapper.find('[data-testid="pr-tab-code-quality"]').exists()).toBe(true)
    expect(capabilityAvailable).toHaveBeenCalledWith('code-insights')
  })

  it('pins the workspace to this pull request, from the scope the page already has', async () => {
    const wrapper = await mountView()

    await wrapper.get('[data-testid="pr-tab-code-quality"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('.pr-code-quality-stub').text()).toBe('client-1|4|4821')
  })

  it('loads nothing until the tab is opened', async () => {
    // Otherwise it is three requests nobody asked for while looking at the stats.
    const wrapper = await mountView()

    expect(wrapper.find('.pr-code-quality-stub').exists()).toBe(false)
  })

  it('is absent without the licence rather than present and empty', async () => {
    capabilityAvailable.mockReturnValue(false)

    const wrapper = await mountView()

    expect(wrapper.find('[data-testid="pr-tab-code-quality"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="pr-tab-stats"]').exists()).toBe(true)
  })

  it('leaves the other tabs alone', async () => {
    const wrapper = await mountView()

    const labels = wrapper.findAll('button.pr-tab-btn').map((button) => button.text())
    expect(labels).toEqual(['Stats', 'Conversation', 'Browser', 'Code Quality'])
  })
})
