// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'

const mockGet = vi.fn()
let assignedRole: 0 | 1 | null = 0

vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('@/composables/useSession', () => ({
  useSession: () => ({
    hasClientRole: (clientId: string, minRole: 0 | 1) =>
      assignedRole !== null && clientId === 'client-1' && assignedRole >= minRole,
  }),
}))

vi.mock('markdown-it', () => ({
  default: vi.fn().mockImplementation(() => ({
    render: (value: string) => `<p>${value}</p>`,
  })),
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

async function mountSection() {
  const { default: ReviewHistorySection } = await import('@/components/ReviewHistorySection.vue')
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
    mockGet.mockImplementation((path: string) => {
      if (path === '/jobs') {
        return Promise.resolve({ data: jobsPayload })
      }

      return Promise.resolve({ data: [] })
    })
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
})