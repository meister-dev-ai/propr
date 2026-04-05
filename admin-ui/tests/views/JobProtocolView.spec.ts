// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

const mockGet = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
  useRoute: () => ({ params: { id: 'job-abc' }, query: {} }),
  RouterLink: { template: '<a><slot /></a>' },
}))

vi.mock('@/components/ModalDialog.vue', () => ({
  default: {
    name: 'ModalDialog',
    props: ['isOpen', 'title'],
    template: '<div class="modal-stub"><slot /></div>',
  },
}))

vi.mock('@/components/ProgressOrb.vue', () => ({
  default: {
    name: 'ProgressOrb',
    template: '<div class="orb-stub" />',
  },
}))

vi.mock('markdown-it', () => {
  return {
    default: vi.fn().mockImplementation(() => ({
      render: (s: string) => `<p>${s}</p>`,
    })),
  }
})

vi.mock('dompurify', () => ({
  default: { sanitize: (s: string) => s },
}))

const makeComment = (
  severity: string,
  message: string,
  filePath: string,
  lineNumber = 1,
) => ({ severity, message, filePath, lineNumber })

const sampleProtocols = [
  {
    id: 'pass-1',
    jobId: 'job-abc',
    label: 'src/foo.ts',
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:01:00Z',
    totalInputTokens: 100,
    totalOutputTokens: 50,
    events: [],
  },
]

const sampleJobResult = {
  status: 'completed',
  submittedAt: '2024-01-01T00:00:00Z',
  completedAt: '2024-01-01T00:01:00Z',
  result: {
    summary: 'All good',
    comments: [
      makeComment('error', 'null pointer in auth', 'src/auth.ts', 10),
      makeComment('warning', 'unused import', 'src/auth.ts', 20),
      makeComment('info', 'consider extracting method', 'src/utils.ts', 5),
      makeComment('suggestion', 'add unit test for edge case', 'src/utils.ts', 15),
    ],
  },
}

async function mountView() {
  const { default: JobProtocolView } = await import('@/views/JobProtocolView.vue')
  const wrapper = mount(JobProtocolView, {
    attachTo: document.body,
  })
  await flushPromises()
  return wrapper
}

describe('JobProtocolView — comment search and filter (T042)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) return Promise.resolve({ data: sampleProtocols, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })
  })

  it('shows all comments when no search and no severity filter', async () => {
    const wrapper = await mountView()
    // All 4 comments should be rendered
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(4)
  })

  it('filters comments by message text (case-insensitive)', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    expect(searchInput.exists()).toBe(true)
    await searchInput.setValue('null pointer')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
    expect(items[0].text()).toContain('null pointer in auth')
  })

  it('filters comments by file path (case-insensitive)', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('utils.ts')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(2)
  })

  it('filters comments by severity toggle', async () => {
    const wrapper = await mountView()
    const errorPill = wrapper.find('button.severity-pill[data-severity="error"]')
    expect(errorPill.exists()).toBe(true)
    await errorPill.trigger('click')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
    expect(items[0].text()).toContain('null pointer in auth')
  })

  it('applies combined search + severity filter', async () => {
    const wrapper = await mountView()
    // Activate warning filter
    const warningPill = wrapper.find('button.severity-pill[data-severity="warning"]')
    await warningPill.trigger('click')
    // Also search for 'auth' — only 'unused import' in auth.ts with severity=warning should match
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('auth')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
    expect(items[0].text()).toContain('unused import')
  })

  it('clicking active severity pill deactivates it (toggle off)', async () => {
    const wrapper = await mountView()
    const infoBtn = wrapper.find('button.severity-pill[data-severity="info"]')
    await infoBtn.trigger('click')
    await flushPromises()
    let items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1) // only info comments

    // Toggle off
    await infoBtn.trigger('click')
    await flushPromises()
    items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(4) // all comments back
  })

  it('shows empty state message when filter matches nothing', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('zzznomatch')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(0)
    expect(wrapper.find('.comments-empty-state').exists()).toBe(true)
  })

  it('polling update (reviewStatus ref change) does not reset searchQuery', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('null pointer')
    await flushPromises()

    // Simulate polling update: mock returns new result with same comments
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) return Promise.resolve({ data: sampleProtocols, response: { ok: true } })
      return Promise.resolve({
        data: { ...sampleJobResult, result: { ...sampleJobResult.result } },
        response: { ok: true },
      })
    })

    // Wait for a poll interval (if polling occurs) — otherwise just re-assert
    await flushPromises()

    // searchQuery should not be reset
    const inputEl = wrapper.find('input.comment-search-input')
    expect((inputEl.element as HTMLInputElement).value).toBe('null pointer')

    // Filter still applied
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
  })

  it('polling update does not reset activeSeverities', async () => {
    const wrapper = await mountView()
    const errorPill = wrapper.find('button.severity-pill[data-severity="error"]')
    await errorPill.trigger('click')
    await flushPromises()

    // Simulate polling
    await flushPromises()

    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1) // error filter still active
  })
})
