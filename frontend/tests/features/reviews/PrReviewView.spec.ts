// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ref } from 'vue'
import PrReviewView from '@/features/reviews/views/PrReviewView.vue'
import type { PrReviewViewDto } from '@/services/jobsService'

const routeQuery = ref<Record<string, string | undefined>>({})
const getPrViewMock = vi.fn()

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
  return {
    ...actual,
    useRoute: () => ({ get query() { return routeQuery.value } }),
    useRouter: () => ({ push: vi.fn(), replace: vi.fn() }),
  }
})

vi.mock('@/services/jobsService', () => ({
  getPrView: (...args: unknown[]) => getPrViewMock(...args),
}))

// The retained composable is exercised in its own and the tab specs; here we only need the
// tabbed shell to mount, so the child retained tabs are stubbed to avoid network plumbing.
vi.mock('@/features/reviews/components/RetainedConversationTab.vue', () => ({
  default: {
    name: 'RetainedConversationTab',
    props: ['retained'],
    template: '<div data-testid="conversation-tab-stub" />',
  },
}))

vi.mock('@/features/reviews/components/RetainedBrowserTab.vue', () => ({
  default: {
    name: 'RetainedBrowserTab',
    props: ['retained'],
    template: '<div data-testid="browser-tab-stub" />',
  },
}))

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: vi.fn().mockResolvedValue({ data: [], error: undefined, response: { ok: true, status: 200 } }) }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

const sampleData: PrReviewViewDto = {
  pullRequestId: 42,
  repositoryId: 'repo-a',
  providerProjectKey: 'proj-x',
  totalJobs: 1,
  totalInputTokens: 1000,
  totalOutputTokens: 200,
  originatedMemoryCount: 0,
  contributedMemoryCount: 0,
  breakdownConsistent: true,
  aggregatedTokenBreakdown: [],
  jobs: [],
  originatedMemories: [],
  contributedMemories: [],
} as unknown as PrReviewViewDto

const resolvableQuery = {
  clientId: '1',
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'proj-x',
  repositoryId: 'repo-a',
  pullRequestId: '42',
}

function mountView() {
  return mount(PrReviewView, {
    global: {
      stubs: {
        RouterLink: { template: '<a><slot /></a>' },
        TokenBreakdownTable: true,
      },
    },
  })
}

describe('PrReviewView', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    routeQuery.value = { ...resolvableQuery }
    getPrViewMock.mockResolvedValue(sampleData)
  })

  it('shows the Stats tab by default with the PR header and Review Jobs section', async () => {
    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.find('[data-testid="pr-tab-stats"]').classes()).toContain('tab-active')
    expect(wrapper.find('[data-testid="pr-panel-stats"]').isVisible()).toBe(true)
    expect(wrapper.text()).toContain('PR #42')
    expect(wrapper.text()).toContain('Review Jobs')
    expect(wrapper.text()).toContain('Memory Records')

    // The retained tabs exist but their panels are not the active one.
    expect(wrapper.find('[data-testid="pr-panel-conversation"]').isVisible()).toBe(false)
    expect(wrapper.find('[data-testid="pr-panel-browser"]').isVisible()).toBe(false)
  })

  it('switches to the Conversation tab and reveals the conversation panel', async () => {
    const wrapper = mountView()
    await flushPromises()

    await wrapper.find('[data-testid="pr-tab-conversation"]').trigger('click')

    expect(wrapper.find('[data-testid="pr-tab-conversation"]').classes()).toContain('tab-active')
    expect(wrapper.find('[data-testid="pr-panel-conversation"]').isVisible()).toBe(true)
    expect(wrapper.find('[data-testid="pr-panel-stats"]').isVisible()).toBe(false)
    expect(wrapper.find('[data-testid="conversation-tab-stub"]').exists()).toBe(true)
  })

  it('switches to the Browser tab and reveals the browser panel', async () => {
    const wrapper = mountView()
    await flushPromises()

    await wrapper.find('[data-testid="pr-tab-browser"]').trigger('click')

    expect(wrapper.find('[data-testid="pr-tab-browser"]').classes()).toContain('tab-active')
    expect(wrapper.find('[data-testid="pr-panel-browser"]').isVisible()).toBe(true)
    expect(wrapper.find('[data-testid="browser-tab-stub"]').exists()).toBe(true)
  })

  it('loads the retained data once and shares the same instance with both tabs', async () => {
    const wrapper = mountView()
    await flushPromises()

    const conversation = wrapper.findComponent({ name: 'RetainedConversationTab' })
    const browser = wrapper.findComponent({ name: 'RetainedBrowserTab' })
    expect(conversation.props('retained')).toBeTruthy()
    // Both tabs receive the exact same composable instance (single fetch).
    expect(conversation.props('retained')).toBe(browser.props('retained'))
  })

  it('creates a fresh retained instance when the route identity changes to a different pull request', async () => {
    const wrapper = mountView()
    await flushPromises()

    const conversation = wrapper.findComponent({ name: 'RetainedConversationTab' })
    const first = conversation.props('retained')
    expect(first).toBeTruthy()

    // Navigate to a different pull request on the same component instance.
    routeQuery.value = { ...resolvableQuery, repositoryId: 'repo-b', pullRequestId: '43' }
    await flushPromises()

    const second = conversation.props('retained')
    expect(second).toBeTruthy()
    // The identity changed, so a new composable instance is created and loaded rather than leaving the
    // previous pull request's retained data on screen.
    expect(second).not.toBe(first)
  })
})
