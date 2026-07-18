// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const listJobsMock = vi.fn()
const restartJobMock = vi.fn()
const stopJobMock = vi.fn()
const listBlockedPrsMock = vi.fn()
const blockPrMock = vi.fn()
const unblockPrMock = vi.fn()
let assignedRole: 0 | 1 | null = 0

vi.mock('@/services/jobsService', () => ({
  listJobs: listJobsMock,
  restartJob: restartJobMock,
  stopJob: stopJobMock,
  listBlockedPrs: listBlockedPrsMock,
  blockPr: blockPrMock,
  unblockPr: unblockPrMock,
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: (clientId: string, minRole: 0 | 1) =>
      assignedRole !== null && clientId === 'client-1' && assignedRole >= minRole,
  }),
}))

vi.mock('markdown-it', () => ({
  // vitest 4: a mock used with `new` must be a class/function, not an arrow factory.
  default: class {
    render(value: string) {
      return `<p>${value}</p>`
    }
  },
}))

vi.mock('dompurify', () => ({
  default: { sanitize: (value: string) => value },
}))

const jobsPayload = {
  items: [
    {
      id: 'job-1',
      clientId: 'client-1',
      organizationUrl: 'https://dev.azure.com/example',
      projectId: 'project-a',
      repositoryId: 'repo-a',
      pullRequestId: 42,
      iterationId: 1,
      status: 'completed',
      submittedAt: '2026-04-02T10:00:00Z',
      processingStartedAt: '2026-04-02T10:01:00Z',
      completedAt: '2026-04-02T10:02:00Z',
      resultSummary: 'Review summary',
      errorMessage: null,
      totalInputTokens: 1200,
      totalOutputTokens: 300,
      prTitle: 'Add feature',
      prRepositoryName: 'repo-a',
      prSourceBranch: 'feature/test',
      prTargetBranch: 'main',
    },
  ],
}

// Job with a full provider identity so its PR group key matches a blocked-PR DTO.
function identifiedJob(overrides: Record<string, unknown> = {}) {
  return {
    id: 'job-1',
    clientId: 'client-1',
    providerScopePath: 'https://dev.azure.com/example',
    providerProjectKey: 'project-a',
    repositoryId: 'repo-a',
    pullRequestId: 42,
    iterationId: 1,
    status: 'processing',
    submittedAt: '2026-04-02T10:00:00Z',
    processingStartedAt: '2026-04-02T10:01:00Z',
    completedAt: null,
    resultSummary: null,
    errorMessage: null,
    prTitle: 'Add feature',
    prRepositoryName: 'repo-a',
    ...overrides,
  }
}

const blockedDto = {
  id: 'block-1',
  clientId: 'client-1',
  providerScopePath: 'https://dev.azure.com/example',
  providerProjectKey: 'project-a',
  repositoryId: 'repo-a',
  pullRequestId: 42,
  blockedByUserId: 'user-1',
  blockedAt: '2026-04-02T10:00:00Z',
  reason: null,
}

async function mountSection() {
  const { default: ReviewHistorySection } = await import('@/features/reviews/components/ReviewHistorySection.vue')
  return mount(ReviewHistorySection, {
    global: {
      stubs: {
        ModalDialog: { template: '<div><slot /></div>' },
        ProgressOrb: { template: '<div class="orb-stub" />' },
      },
    },
  })
}

describe('ReviewHistorySection', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    assignedRole = 0
    listJobsMock.mockResolvedValue(jobsPayload)
    restartJobMock.mockResolvedValue(undefined)
    stopJobMock.mockResolvedValue(undefined)
    listBlockedPrsMock.mockResolvedValue([])
    blockPrMock.mockResolvedValue(undefined)
    unblockPrMock.mockResolvedValue(undefined)
  })

  it('shows protocol and PR view links for assigned client users', async () => {
    const wrapper = await mountSection()
    await flushPromises()

    expect(wrapper.find('.protocol-btn').exists()).toBe(true)
    expect(wrapper.find('.pr-view-btn').exists()).toBe(true)
  })

  it('shows protocol and PR view links for client administrators', async () => {
    assignedRole = 1

    const wrapper = await mountSection()
    await flushPromises()

    expect(wrapper.find('.protocol-btn').exists()).toBe(true)
    expect(wrapper.find('.pr-view-btn').exists()).toBe(true)
  })

  it('hides protocol and PR view links for unassigned users', async () => {
    assignedRole = null

    const wrapper = await mountSection()
    await flushPromises()

    expect(wrapper.find('.protocol-btn').exists()).toBe(false)
    expect(wrapper.find('.pr-view-btn').exists()).toBe(false)
  })

  describe('Stop button', () => {
    it('shows the Stop button for administrators on a running job', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob({ status: 'processing' })] })

      const wrapper = await mountSection()
      await flushPromises()

      expect(wrapper.find('.stop-btn').exists()).toBe(true)
    })

    it('shows the Stop button for a queued (pending) job', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob({ status: 'pending', processingStartedAt: null })] })

      const wrapper = await mountSection()
      await flushPromises()

      expect(wrapper.find('.stop-btn').exists()).toBe(true)
    })

    it('hides the Stop button for non-administrators', async () => {
      assignedRole = 0
      listJobsMock.mockResolvedValue({ items: [identifiedJob({ status: 'processing' })] })

      const wrapper = await mountSection()
      await flushPromises()

      expect(wrapper.find('.stop-btn').exists()).toBe(false)
    })

    it('hides the Stop button for terminal jobs', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob({ status: 'completed', completedAt: '2026-04-02T10:02:00Z' })] })

      const wrapper = await mountSection()
      await flushPromises()

      expect(wrapper.find('.stop-btn').exists()).toBe(false)
    })

    it('calls the stop service and reloads when clicked', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob({ status: 'processing' })] })

      const wrapper = await mountSection()
      await flushPromises()
      listJobsMock.mockClear()

      await wrapper.find('.stop-btn').trigger('click')
      await flushPromises()

      expect(stopJobMock).toHaveBeenCalledWith('job-1')
      expect(listJobsMock).toHaveBeenCalled() // reloaded after stop
    })
  })

  describe('Block menu', () => {
    it('shows the block overflow menu for administrators', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })

      const wrapper = await mountSection()
      await flushPromises()

      expect(wrapper.find('.pr-block-menu .overflow-menu-trigger').exists()).toBe(true)
    })

    it('hides the block overflow menu for non-administrators', async () => {
      assignedRole = 0
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })

      const wrapper = await mountSection()
      await flushPromises()

      expect(wrapper.find('.pr-block-menu').exists()).toBe(false)
    })

    it('labels the item "Block PR" when the PR is not blocked', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })
      listBlockedPrsMock.mockResolvedValue([])

      const wrapper = await mountSection()
      await flushPromises()

      await wrapper.find('.pr-block-menu .overflow-menu-trigger').trigger('click')
      await flushPromises()

      expect(wrapper.find('.pr-block-menu .overflow-menu-item').text()).toContain('Block PR')
    })

    it('labels the item "Unblock PR" when the PR is already blocked', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })
      listBlockedPrsMock.mockResolvedValue([blockedDto])

      const wrapper = await mountSection()
      await flushPromises()
      await flushPromises()

      await wrapper.find('.pr-block-menu .overflow-menu-trigger').trigger('click')
      await flushPromises()

      expect(wrapper.find('.pr-block-menu .overflow-menu-item').text()).toContain('Unblock PR')
    })

    it('shows a Blocked badge on a blocked PR', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })
      listBlockedPrsMock.mockResolvedValue([blockedDto])

      const wrapper = await mountSection()
      await flushPromises()
      await flushPromises()

      expect(wrapper.find('.blocked-badge').exists()).toBe(true)
      expect(wrapper.find('.blocked-badge').text()).toContain('Blocked')
    })

    it('shows the Blocked badge to non-administrators but still hides the block menu', async () => {
      assignedRole = 0
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })
      listBlockedPrsMock.mockResolvedValue([blockedDto])

      const wrapper = await mountSection()
      await flushPromises()
      await flushPromises()

      expect(wrapper.find('.blocked-badge').exists()).toBe(true)
      expect(wrapper.find('.pr-block-menu').exists()).toBe(false)
    })

    it('calls block and refreshes state when an unblocked PR is blocked', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })
      listBlockedPrsMock.mockResolvedValue([])

      const wrapper = await mountSection()
      await flushPromises()
      listBlockedPrsMock.mockClear()

      await wrapper.find('.pr-block-menu .overflow-menu-trigger').trigger('click')
      await flushPromises()
      await wrapper.find('.pr-block-menu .overflow-menu-item').trigger('click')
      await flushPromises()

      expect(blockPrMock).toHaveBeenCalledWith('client-1', {
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-a',
        repositoryId: 'repo-a',
        pullRequestId: 42,
      })
      expect(listBlockedPrsMock).toHaveBeenCalled() // refreshed after blocking
    })

    it('calls unblock when a blocked PR is unblocked', async () => {
      assignedRole = 1
      listJobsMock.mockResolvedValue({ items: [identifiedJob()] })
      listBlockedPrsMock.mockResolvedValue([blockedDto])

      const wrapper = await mountSection()
      await flushPromises()
      await flushPromises()

      await wrapper.find('.pr-block-menu .overflow-menu-trigger').trigger('click')
      await flushPromises()
      await wrapper.find('.pr-block-menu .overflow-menu-item').trigger('click')
      await flushPromises()

      expect(unblockPrMock).toHaveBeenCalledWith('client-1', {
        providerScopePath: 'https://dev.azure.com/example',
        providerProjectKey: 'project-a',
        repositoryId: 'repo-a',
        pullRequestId: 42,
      })
    })
  })
})
