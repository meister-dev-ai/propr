// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
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

const diffViewerStub = {
  name: 'JobProtocolDiffViewer',
  props: ['fileResultId', 'diff', 'loading', 'diffError', 'onRetry'],
  template: '<div data-testid="diff-viewer-stub">{{ diff?.unifiedDiff }}</div>',
}

async function mountWithLoadedData() {
  const retained = useRetainedPrData(identity)
  await retained.load()
  return mount(RetainedBrowserTab, {
    props: { retained, clientId: identity.clientId },
    global: { stubs: { JobProtocolDiffViewer: diffViewerStub } },
  })
}

describe('RetainedBrowserTab', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('renders the empty notice when retention returns nothing', async () => {
    getMock.mockResolvedValue(okResponse([]))

    const wrapper = await mountWithLoadedData()

    expect(wrapper.find('[data-testid="retained-empty"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="retained-error"]').exists()).toBe(false)
  })

  it('lists the retained files and loads the diff for the selected file', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) {
        return Promise.resolve(
          okResponse([{ threadId: 't1', filePath: 'src/foo.ts', line: 5, status: 'Active', comments: [{ body: 'note', isAiAuthored: true }] }]),
        )
      }
      if (path.endsWith('/files')) {
        return Promise.resolve(okResponse([{ filePath: 'src/foo.ts', changeType: 'Modified', isBinary: false, revisionKey: 'rev-1' }]))
      }
      // file-diff
      return Promise.resolve(okResponse({ filePath: 'src/foo.ts', unifiedDiff: '@@ -1 +1 @@ changed', changeType: 'Modified', isBinary: false }))
    })

    const wrapper = await mountWithLoadedData()

    // No file selected yet → prompt to select.
    expect(wrapper.find('[data-testid="retained-no-selection"]').exists()).toBe(true)

    await wrapper.findAll('[data-testid="retained-file-item"]')[0].trigger('click')
    await flushPromises()

    // The file-diff endpoint was requested for the chosen file.
    expect(
      getMock.mock.calls.some(
        ([path, opts]) =>
          typeof path === 'string'
          && path.endsWith('/file-diff')
          && (opts as { params: { query: { filePath: string } } }).params.query.filePath === 'src/foo.ts',
      ),
    ).toBe(true)

    // The adapted diff is surfaced through the diff viewer.
    const stub = wrapper.find('[data-testid="diff-viewer-stub"]')
    expect(stub.exists()).toBe(true)
    expect(stub.text()).toContain('@@ -1 +1 @@ changed')
  })

  it('ignores a previous diff response when a later file is selected', async () => {
    let resolveFirst!: (value: ReturnType<typeof okResponse>) => void
    getMock.mockImplementation((path: string, options: { params: { query: { filePath?: string } } }) => {
      if (path.endsWith('/threads')) return Promise.resolve(okResponse([]))
      if (path.endsWith('/files')) return Promise.resolve(okResponse([{ filePath: 'first.ts' }, { filePath: 'second.ts' }]))
      if (options.params.query.filePath === 'first.ts') return new Promise(resolve => { resolveFirst = resolve })
      return Promise.resolve(okResponse({ filePath: 'second.ts', unifiedDiff: 'second diff' }))
    })
    const wrapper = await mountWithLoadedData()
    await wrapper.get('[data-file-path="first.ts"]').trigger('click')
    await wrapper.get('[data-file-path="second.ts"]').trigger('click')
    await flushPromises()
    resolveFirst(okResponse({ filePath: 'first.ts', unifiedDiff: 'first diff' }))
    await flushPromises()
    expect(wrapper.get('[data-testid="diff-viewer-stub"]').text()).toBe('second diff')
  })

  it('preserves the selected diff while the file still matches the filters', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) return Promise.resolve(okResponse([]))
      if (path.endsWith('/files')) return Promise.resolve(okResponse([{ filePath: 'src/Auth.ts' }, { filePath: 'src/Cache.ts' }]))
      return Promise.resolve(okResponse({ filePath: 'src/Auth.ts', unifiedDiff: 'auth diff' }))
    })
    const wrapper = await mountWithLoadedData()
    await wrapper.get('[data-file-path="src/Auth.ts"]').trigger('click')
    await flushPromises()
    await wrapper.get('input[aria-label="Search files"]').setValue('AUTH')
    expect(wrapper.findAll('[data-testid="retained-file-item"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="diff-viewer-stub"]').text()).toBe('auth diff')
    await wrapper.get('[data-testid="browser-clear-filters"]').trigger('click')
    expect(wrapper.findAll('[data-testid="retained-file-item"]')).toHaveLength(2)
    expect(wrapper.get('[data-testid="diff-viewer-stub"]').text()).toBe('auth diff')
  })

  it('identifies a refresh and retained metadata after a refresh failure', async () => {
    getMock.mockImplementation((path: string) => Promise.resolve(okResponse(
      path.endsWith('/files') ? [{ filePath: 'src/Auth.ts' }] : [],
    )))
    const wrapper = await mountWithLoadedData()
    const finding = {
      id: 'f1', clientId: 'client-1', repositoryId: 'repo-a', pullRequestId: 42, jobId: 'job-1',
      filePath: 'src/Auth.ts', lineNumber: 7, severity: 'Warning', message: 'Validate the token.',
      coreTags: ['security'], disposition: 'Dismissed', rejectionReason: 'Redundant',
      providerThreadId: 'thread-1', observedAt: '2026-01-01T00:00:00Z',
    }
    await wrapper.setProps({ findings: [finding], metadataLoading: true })
    expect(wrapper.get('[role="status"]').text()).toBe('Refreshing finding details…')
    await wrapper.setProps({ metadataLoading: false, metadataError: 'Refresh unavailable' })
    expect(wrapper.get('[role="status"]').text()).toContain('Showing the last loaded details.')
    expect(wrapper.get('[role="status"]').text()).toContain('Refresh unavailable')
    await wrapper.setProps({ findings: [] })
    expect(wrapper.get('[role="status"]').text()).toContain('Finding details unavailable.')
    expect(wrapper.get('[role="status"]').text()).not.toContain('Showing the last loaded details.')
    wrapper.unmount()
  })

  it.each([false, true])('clears a filtered-out selection, including an outstanding diff request (pending: %s)', async pending => {
    let resolveDiff!: (value: ReturnType<typeof okResponse>) => void
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) return Promise.resolve(okResponse([]))
      if (path.endsWith('/files')) return Promise.resolve(okResponse([{ filePath: 'src/Auth.ts' }, { filePath: 'src/Cache.ts' }]))
      return new Promise(resolve => { resolveDiff = resolve })
    })
    const wrapper = await mountWithLoadedData()
    await wrapper.get('[data-file-path="src/Auth.ts"]').trigger('click')
    if (!pending) {
      resolveDiff(okResponse({ filePath: 'src/Auth.ts', unifiedDiff: 'auth diff' }))
      await flushPromises()
    }
    await wrapper.get('input[aria-label="Search files"]').setValue('CACHE')
    expect(wrapper.findAll('[data-testid="retained-file-item"]')).toHaveLength(1)
    expect(wrapper.find('[data-testid="retained-no-selection"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="diff-viewer-stub"]').exists()).toBe(false)
    await wrapper.get('[data-testid="browser-clear-filters"]').trigger('click')
    if (pending) {
      resolveDiff(okResponse({ filePath: 'src/Auth.ts', unifiedDiff: 'obsolete diff' }))
      await flushPromises()
    }
    expect(wrapper.find('[data-testid="retained-no-selection"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="diff-viewer-stub"]').exists()).toBe(false)
    expect(wrapper.findAll('.retained-file-item--active')).toHaveLength(0)
    wrapper.unmount()
  })

  it.each([false, true])('clears a selected revision when a newer retained revision replaces it (pending: %s)', async pending => {
    let resolveDiff!: (value: ReturnType<typeof okResponse>) => void
    getMock.mockImplementation((path: string, options: { params: { query: { revisionKey?: string } } }) => {
      if (path.endsWith('/threads')) return Promise.resolve(okResponse([]))
      if (path.endsWith('/files')) return Promise.resolve(okResponse([{ filePath: 'src/Auth.ts', revisionKey: 'rev-1' }]))
      if (options.params.query.revisionKey === 'rev-2') return Promise.resolve(okResponse({ filePath: 'src/Auth.ts', unifiedDiff: 'current diff' }))
      return new Promise(resolve => { resolveDiff = resolve })
    })
    const wrapper = await mountWithLoadedData()
    await wrapper.get('[data-file-path="src/Auth.ts"]').trigger('click')
    if (!pending) {
      resolveDiff(okResponse({ filePath: 'src/Auth.ts', unifiedDiff: 'earlier diff' }))
      await flushPromises()
    }
    wrapper.props('retained').files.value = [{ filePath: 'src/Auth.ts', revisionKey: 'rev-2' }]
    await flushPromises()
    expect(wrapper.find('[data-testid="retained-no-selection"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="diff-viewer-stub"]').exists()).toBe(false)
    if (pending) {
      resolveDiff(okResponse({ filePath: 'src/Auth.ts', unifiedDiff: 'obsolete diff' }))
      await flushPromises()
    }
    expect(wrapper.find('[data-testid="diff-viewer-stub"]').exists()).toBe(false)
    await wrapper.get('[data-file-path="src/Auth.ts"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="diff-viewer-stub"]').text()).toBe('current diff')
    wrapper.unmount()
  })
})
