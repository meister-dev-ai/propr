// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import RetainedThreadPanel from '@/features/reviews/components/RetainedThreadPanel.vue'
import type { RetainedThread } from '@/features/reviews/composables/useRetainedPrData'

const CLIENT_ID = 'client-1'

const threads: RetainedThread[] = [
  {
    threadId: 't1',
    filePath: 'src/foo.ts',
    line: 12,
    status: 'Active',
    comments: [
      { commentId: 'c1', authorIdentity: 'alice', isAiAuthored: false, body: 'Please **rename** this', publishedAt: '2026-04-25T10:00:00Z' },
      { commentId: 'c2', authorIdentity: 'propr-bot', isAiAuthored: true, body: 'Suggested fix applied', publishedAt: '2026-04-25T10:02:00Z' },
    ],
  },
]

// The global setup stub drops `to`; this local stub preserves it as an href so the trace link
// target can be asserted.
const routerLinkStub = {
  props: ['to'],
  template: '<a :href="to"><slot /></a>',
}

describe('RetainedThreadPanel', () => {
  it('distinguishes human and AI comments using the stored isAiAuthored flag', () => {
    const wrapper = mount(RetainedThreadPanel, { props: { threads, clientId: CLIENT_ID } })

    const humanComments = wrapper.findAll('[data-testid="retained-comment-human"]')
    const aiComments = wrapper.findAll('[data-testid="retained-comment-ai"]')
    expect(humanComments).toHaveLength(1)
    expect(aiComments).toHaveLength(1)
    expect(humanComments[0].text()).toContain('alice')
    expect(aiComments[0].text()).toContain('propr-bot')
    // The AI comment carries a visible AI marker that the human one does not.
    expect(aiComments[0].text()).toContain('AI')
  })

  it('renders the thread status', () => {
    const wrapper = mount(RetainedThreadPanel, { props: { threads, clientId: CLIENT_ID } })

    const status = wrapper.find('[data-testid="retained-thread-status"]')
    expect(status.exists()).toBe(true)
    expect(status.text()).toContain('Active')
  })

  it('renders comment bodies as sanitized markdown', () => {
    const wrapper = mount(RetainedThreadPanel, { props: { threads, clientId: CLIENT_ID } })

    const bodies = wrapper.findAll('.retained-comment-body')
    expect(bodies).toHaveLength(2)
    // Markdown bold becomes real HTML rather than literal asterisks.
    expect(bodies[0].html()).toContain('<strong>rename</strong>')
    expect(bodies[0].text()).not.toContain('**rename**')
    // Bodies are wrapped in the shared markdown-content container.
    expect(bodies[0].classes()).toContain('markdown-content')
  })

  it('renders markdown lists as HTML list elements', () => {
    const listThread: RetainedThread[] = [
      {
        threadId: 't-list',
        filePath: 'src/bar.ts',
        line: 1,
        status: 'Active',
        comments: [{ commentId: 'lc', isAiAuthored: true, body: '- first\n- second' }],
      },
    ]

    const wrapper = mount(RetainedThreadPanel, { props: { threads: listThread, clientId: CLIENT_ID } })

    const body = wrapper.find('.retained-comment-body')
    expect(body.findAll('li')).toHaveLength(2)
  })

  it('renders dangerous HTML in comment bodies inert (sanitized, no live script/handler)', () => {
    const malicious: RetainedThread[] = [
      {
        threadId: 't-evil',
        filePath: 'src/evil.ts',
        line: 1,
        status: 'Active',
        comments: [
          {
            commentId: 'evil',
            isAiAuthored: false,
            authorIdentity: 'attacker',
            body: 'before <script>window.__pwned = true</script> <img src="x" onerror="window.__pwned = true"> after',
          },
        ],
      },
    ]

    const wrapper = mount(RetainedThreadPanel, { props: { threads: malicious, clientId: CLIENT_ID } })

    const body = wrapper.find('.retained-comment-body')
    // No executable script element and no live element carrying an onerror handler reaches the DOM.
    expect(body.element.querySelector('script')).toBeNull()
    const elementWithHandler = Array.from(body.element.querySelectorAll('*')).find(el =>
      el.hasAttribute('onerror'),
    )
    expect(elementWithHandler).toBeUndefined()
    // The surrounding safe text survives.
    expect(body.text()).toContain('before')
    expect(body.text()).toContain('after')
  })

  it('shows the anchored file path in the header when showFilePath is set', () => {
    const wrapper = mount(RetainedThreadPanel, { props: { threads, clientId: CLIENT_ID, showFilePath: true } })

    const fileLabel = wrapper.find('[data-testid="retained-thread-file"]')
    expect(fileLabel.exists()).toBe(true)
    expect(fileLabel.text()).toContain('src/foo.ts')
  })

  it('renders the empty message when there are no threads', () => {
    const wrapper = mount(RetainedThreadPanel, {
      props: { threads: [], clientId: CLIENT_ID, emptyMessage: 'Nothing retained here.' },
    })

    expect(wrapper.find('[data-testid="retained-thread-empty"]').text()).toContain('Nothing retained here.')
  })

  it('renders a "View trace" link to the originating run when a comment carries originatingJobId', () => {
    const withOrigin: RetainedThread[] = [
      {
        threadId: 't-origin',
        filePath: 'src/foo.ts',
        line: 10,
        status: 'Active',
        comments: [
          { commentId: 'c-ai', authorIdentity: 'propr-bot', isAiAuthored: true, body: 'Surface the error.', originatingJobId: 'job-abc-123' },
        ],
      },
    ]

    const wrapper = mount(RetainedThreadPanel, {
      props: { threads: withOrigin, clientId: CLIENT_ID },
      global: { stubs: { RouterLink: routerLinkStub } },
    })

    const link = wrapper.find('[data-testid="comment-trace-link"]')
    expect(link.exists()).toBe(true)
    expect(link.text()).toContain('View trace')
    expect(link.attributes('href')).toBe('/jobs/job-abc-123/protocol?clientId=client-1')
  })

  it('renders no trace link for a comment without an originatingJobId', () => {
    const withoutOrigin: RetainedThread[] = [
      {
        threadId: 't-plain',
        filePath: 'src/foo.ts',
        line: 10,
        status: 'Active',
        comments: [
          { commentId: 'c-human', authorIdentity: 'alice', isAiAuthored: false, body: 'Looks good.' },
        ],
      },
    ]

    const wrapper = mount(RetainedThreadPanel, {
      props: { threads: withoutOrigin, clientId: CLIENT_ID },
      global: { stubs: { RouterLink: routerLinkStub } },
    })

    expect(wrapper.find('[data-testid="comment-trace-link"]').exists()).toBe(false)
  })
})
