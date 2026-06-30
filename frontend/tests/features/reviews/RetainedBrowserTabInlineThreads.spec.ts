// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { nextTick } from 'vue'
import RetainedBrowserTab from '@/features/reviews/components/RetainedBrowserTab.vue'
import {
  useRetainedPrData,
  type RetainedPrIdentity,
} from '@/features/reviews/composables/useRetainedPrData'

const getMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: getMock }),
  getApiErrorMessage: (error: unknown, fallback: string) => {
    if (error instanceof Error) return error.message
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

// A real unified diff whose NEW side covers lines 39-45. Line 42 (the context `}`) is present;
// line 999 is not, so a thread anchored there must fall back below the diff.
const middlewareDiff = [
  'diff --git a/src/auth/middleware.ts b/src/auth/middleware.ts',
  'index 1111111..2222222 100644',
  '--- a/src/auth/middleware.ts',
  '+++ b/src/auth/middleware.ts',
  '@@ -39,7 +39,9 @@ export function authenticate(req: Request): Principal {',
  '   const token = readBearerToken(req)',
  '   if (!token) {',
  '-    return anonymous()',
  "+    throw new UnauthorizedError('missing bearer token')",
  '   }',
  '-  return verify(token)',
  '+  const principal = verify(token)',
  '+  return principal',
  ' }',
].join('\n')

const anchoredThread = {
  threadId: 'thread-anchored',
  filePath: 'src/auth/middleware.ts',
  line: 42,
  status: 'Fixed',
  comments: [
    {
      commentId: 'c-ai',
      authorIdentity: 'ProPR Reviewer',
      isAiAuthored: true,
      body: 'This handler **swallows** the rejected token error.',
      originatingJobId: 'job-abc-123',
    },
  ],
}

const orphanThread = {
  threadId: 'thread-orphan',
  filePath: 'src/auth/middleware.ts',
  line: 999,
  status: 'Active',
  comments: [
    {
      commentId: 'c-human',
      authorIdentity: 'jane.developer',
      isAiAuthored: false,
      body: 'This comment anchors to a line not present in the rendered diff.',
    },
  ],
}

// The global setup stub drops `to`; this local stub preserves it as an href so the trace link
// target can be asserted on the inline widget.
const routerLinkStub = {
  props: ['to'],
  template: '<a :href="to"><slot /></a>',
}

function arrangeApi(threads: unknown[]) {
  getMock.mockImplementation((path: string) => {
    if (path.endsWith('/threads')) return Promise.resolve(okResponse(threads))
    if (path.endsWith('/files')) {
      return Promise.resolve(
        okResponse([{ filePath: 'src/auth/middleware.ts', changeType: 'modified', isBinary: false, revisionKey: 'rev-2' }]),
      )
    }
    // file-diff
    return Promise.resolve(
      okResponse({ filePath: 'src/auth/middleware.ts', unifiedDiff: middlewareDiff, changeType: 'modified', isBinary: false }),
    )
  })
}

async function mountTabWithThreads(threads: unknown[]): Promise<VueWrapper> {
  arrangeApi(threads)
  const retained = useRetainedPrData(identity)
  await retained.load()
  const wrapper = mount(RetainedBrowserTab, {
    props: { retained, clientId: identity.clientId },
    attachTo: document.body,
    global: { stubs: { RouterLink: routerLinkStub } },
  })
  await wrapper.find('[data-file-path="src/auth/middleware.ts"]').trigger('click')
  await flushPromises()
  // Let the diff render + the inline injection settle (renderDiff runs on nextTick).
  await nextTick()
  await nextTick()
  return wrapper
}

describe('RetainedBrowserTab inline threads', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('renders a file-anchored thread inline at its line inside the diff, with markdown + AI marker + status', async () => {
    const wrapper = await mountTabWithThreads([anchoredThread])

    const diffViewer = wrapper.find('[data-testid="diff-viewer"]')
    expect(diffViewer.exists()).toBe(true)

    // The inline thread is injected *inside* the diff table, anchored at line 42.
    const inline = diffViewer.find('[data-testid="inline-thread"]')
    expect(inline.exists()).toBe(true)
    expect(inline.attributes('data-line')).toBe('42')

    // Body is rendered as sanitized markdown (the **swallows** emphasis becomes a <strong>).
    expect(inline.find('strong').text()).toBe('swallows')

    // AI authorship marker and status are surfaced on the inline widget.
    expect(inline.find('[data-testid="inline-comment-ai"]').exists()).toBe(true)
    expect(inline.find('[data-testid="inline-thread-status"]').text()).toContain('Fixed')

    // The AI comment records its origin, so the inline widget links to that run's trace.
    const traceLink = inline.find('[data-testid="comment-trace-link"]')
    expect(traceLink.exists()).toBe(true)
    expect(traceLink.attributes('href')).toBe('/jobs/job-abc-123/protocol?clientId=client-1')

    // Everything anchored: the below-diff "Unanchored comments" fallback is absent.
    expect(wrapper.find('[data-testid="retained-thread-panel"]').exists()).toBe(false)
  })

  it('keeps a thread whose line is absent from the diff in the below-diff unanchored fallback', async () => {
    const wrapper = await mountTabWithThreads([anchoredThread, orphanThread])

    const diffViewer = wrapper.find('[data-testid="diff-viewer"]')

    // The line-42 thread anchors inline; the line-999 thread does not.
    const inlineLines = diffViewer.findAll('[data-testid="inline-thread"]').map(el => el.attributes('data-line'))
    expect(inlineLines).toEqual(['42'])

    // The orphan thread survives in the below-diff fallback panel.
    const fallback = wrapper.find('[data-testid="retained-thread-panel"]')
    expect(fallback.exists()).toBe(true)
    expect(fallback.text()).toContain('anchors to a line not present')
    // The anchored thread is NOT duplicated in the fallback.
    expect(fallback.text()).not.toContain('swallows')
  })
})
