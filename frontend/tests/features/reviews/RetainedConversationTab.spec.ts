// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import RetainedConversationTab from '@/features/reviews/components/RetainedConversationTab.vue'
import {
  useRetainedPrData,
  type RetainedPrIdentity,
} from '@/features/reviews/composables/useRetainedPrData'

const getMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: getMock }),
  getApiErrorMessage: (error: unknown, fallback: string) => {
    if (error instanceof Error) return error.message
    if (error && typeof error === 'object') {
      const apiError = error as { message?: string }
      return apiError.message ?? fallback
    }
    return fallback
  },
}))

const identity: RetainedPrIdentity = {
  clientId: 'client-1',
  repositoryId: 'repo-a',
  pullRequestId: 42,
}

function okResponse<T>(data: T) {
  return { data, error: undefined, response: { ok: true, status: 200 } }
}

// The global setup stub drops `to`; this local stub preserves it as an href so the trace link
// target can be asserted.
const routerLinkStub = {
  props: ['to'],
  template: '<a :href="to"><slot /></a>',
}

async function mountWithLoadedData() {
  const retained = useRetainedPrData(identity)
  await retained.load()
  return mount(RetainedConversationTab, {
    props: { retained, clientId: identity.clientId },
    global: { stubs: { RouterLink: routerLinkStub } },
  })
}

describe('RetainedConversationTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('shows the combined PR-level and file-anchored threads as one conversation', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) {
        return Promise.resolve(
          okResponse([
            { threadId: 't1', filePath: 'src/foo.ts', line: 10, status: 'Active', comments: [{ body: 'human note', isAiAuthored: false, authorIdentity: 'alice' }] },
            { threadId: 't2', filePath: null, status: 'Active', comments: [{ body: 'pr-level discussion', isAiAuthored: true }] },
          ]),
        )
      }
      return Promise.resolve(okResponse([{ filePath: 'src/foo.ts', changeType: 'Modified', isBinary: false }]))
    })

    const wrapper = await mountWithLoadedData()

    const threads = wrapper.findAll('[data-testid="retained-thread"]')
    expect(threads).toHaveLength(2)
    // The file-anchored thread surfaces its file path in the conversation view.
    expect(wrapper.find('[data-testid="retained-thread-file"]').text()).toContain('src/foo.ts')
    expect(wrapper.text()).toContain('pr-level discussion')
    expect(wrapper.text()).toContain('human note')
  })

  it('surfaces a trace link for an AI comment with provenance and none for a plain comment', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) {
        return Promise.resolve(
          okResponse([
            {
              threadId: 't1',
              filePath: 'src/foo.ts',
              line: 10,
              status: 'Active',
              comments: [
                { commentId: 'c-ai', body: 'AI finding', isAiAuthored: true, authorIdentity: 'propr-bot', originatingJobId: 'job-abc-123' },
                { commentId: 'c-human', body: 'human note', isAiAuthored: false, authorIdentity: 'alice' },
              ],
            },
          ]),
        )
      }
      return Promise.resolve(okResponse([{ filePath: 'src/foo.ts', changeType: 'Modified', isBinary: false }]))
    })

    const wrapper = await mountWithLoadedData()

    const aiComment = wrapper.find('[data-testid="retained-comment-ai"]')
    const aiTraceLink = aiComment.find('[data-testid="comment-trace-link"]')
    expect(aiTraceLink.exists()).toBe(true)
    expect(aiTraceLink.attributes('href')).toBe('/jobs/job-abc-123/protocol?clientId=client-1')

    const humanComment = wrapper.find('[data-testid="retained-comment-human"]')
    expect(humanComment.find('[data-testid="comment-trace-link"]').exists()).toBe(false)
  })

  it('renders the empty notice when retention returns nothing', async () => {
    getMock.mockResolvedValue(okResponse([]))

    const wrapper = await mountWithLoadedData()

    expect(wrapper.find('[data-testid="retained-empty"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="retained-error"]').exists()).toBe(false)
  })
})
