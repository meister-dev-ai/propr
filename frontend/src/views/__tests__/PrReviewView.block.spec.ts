// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'

const routeQuery = ref<Record<string, string | undefined>>({})
const getPrViewMock = vi.fn()
const listBlockedPrsMock = vi.fn()
const blockPrMock = vi.fn()
const unblockPrMock = vi.fn()
let assignedAdmin = true

vi.mock('vue-router', async () => {
  const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
  return {
    ...actual,
    useRoute: () => ({ query: routeQuery.value }),
  }
})

vi.mock('@/services/jobsService', () => ({
  getPrView: (...args: unknown[]) => getPrViewMock(...args),
  listBlockedPrs: (...args: unknown[]) => listBlockedPrsMock(...args),
  blockPr: (...args: unknown[]) => blockPrMock(...args),
  unblockPr: (...args: unknown[]) => unblockPrMock(...args),
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: (clientId: string, minRole: number) => clientId === 'client-1' && (assignedAdmin ? 1 : 0) >= minRole,
  }),
}))

// The retained-archive composable is exercised elsewhere; stub it so the header/menu can mount.
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

const expectedIdentity = {
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: 'repo-a',
  pullRequestId: 42,
}

const blockedDto = {
  id: 'block-1',
  clientId: 'client-1',
  ...expectedIdentity,
  blockedByUserId: 'user-1',
  blockedAt: '2026-04-02T10:00:00Z',
  reason: null,
}

async function mountView() {
  const { default: PrReviewView } = await import('@/features/reviews/views/PrReviewView.vue')
  return mount(PrReviewView, {
    global: {
      stubs: {
        RouterLink: {
          props: ['to'],
          template: '<a><slot /></a>',
        },
        TokenBreakdownTable: { template: '<div />' },
        RetainedConversationTab: { template: '<div />' },
        RetainedBrowserTab: { template: '<div />' },
      },
    },
  })
}

describe('PrReviewView — block/unblock menu', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    assignedAdmin = true
    routeQuery.value = { ...identityQuery }
    getPrViewMock.mockResolvedValue({
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
    })
    listBlockedPrsMock.mockResolvedValue([])
    blockPrMock.mockResolvedValue(undefined)
    unblockPrMock.mockResolvedValue(undefined)
  })

  it('hides the overflow menu for non-administrators', async () => {
    assignedAdmin = false
    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('.pr-actions-menu').exists()).toBe(false)
  })

  it('shows the overflow menu for administrators with a "Block PR" label when unblocked', async () => {
    listBlockedPrsMock.mockResolvedValue([])

    const wrapper = await mountView()
    await flushPromises()

    expect(wrapper.find('.pr-actions-menu .overflow-menu-trigger').exists()).toBe(true)

    await wrapper.find('.pr-actions-menu .overflow-menu-trigger').trigger('click')
    await flushPromises()

    expect(wrapper.find('.overflow-menu-item').text()).toContain('Block PR')
  })

  it('labels the item "Unblock PR" when the PR is already blocked', async () => {
    listBlockedPrsMock.mockResolvedValue([blockedDto])

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.find('.pr-actions-menu .overflow-menu-trigger').trigger('click')
    await flushPromises()

    expect(wrapper.find('.overflow-menu-item').text()).toContain('Unblock PR')
  })

  it('calls block and refreshes state when an unblocked PR is blocked', async () => {
    listBlockedPrsMock.mockResolvedValue([])

    const wrapper = await mountView()
    await flushPromises()
    listBlockedPrsMock.mockClear()

    await wrapper.find('.pr-actions-menu .overflow-menu-trigger').trigger('click')
    await flushPromises()
    await wrapper.find('.overflow-menu-item').trigger('click')
    await flushPromises()

    expect(blockPrMock).toHaveBeenCalledWith('client-1', expectedIdentity)
    expect(listBlockedPrsMock).toHaveBeenCalled() // refreshed after blocking
  })

  it('calls unblock when a blocked PR is unblocked', async () => {
    listBlockedPrsMock.mockResolvedValue([blockedDto])

    const wrapper = await mountView()
    await flushPromises()

    await wrapper.find('.pr-actions-menu .overflow-menu-trigger').trigger('click')
    await flushPromises()
    await wrapper.find('.overflow-menu-item').trigger('click')
    await flushPromises()

    expect(unblockPrMock).toHaveBeenCalledWith('client-1', expectedIdentity)
  })
})
