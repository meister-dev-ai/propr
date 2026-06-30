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
})
