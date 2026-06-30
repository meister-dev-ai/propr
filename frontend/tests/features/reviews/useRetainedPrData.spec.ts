// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
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

describe('useRetainedPrData', () => {
  beforeEach(() => {
    vi.clearAllMocks()
  })

  it('loads threads and files for the identity without resolving a connection', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) {
        return Promise.resolve(
          okResponse([
            { threadId: 't1', filePath: 'src/foo.ts', line: 10, status: 'Active', comments: [{ body: 'hi', isAiAuthored: false }, { body: 'fix', isAiAuthored: true }] },
            { threadId: 't2', filePath: null, status: 'Active', comments: [{ body: 'pr-level', isAiAuthored: true }] },
          ]),
        )
      }
      return Promise.resolve(okResponse([{ filePath: 'src/foo.ts', changeType: 'Modified', isBinary: false }]))
    })

    const retained = useRetainedPrData(identity)
    await retained.load()

    // The reads are keyed on repositoryId + pullRequestId; no connectionId is sent.
    const threadsCall = getMock.mock.calls.find(([path]) => typeof path === 'string' && path.endsWith('/threads'))
    expect(threadsCall).toBeDefined()
    const threadsQuery = (threadsCall?.[1] as { params: { query: Record<string, unknown> } }).params.query
    expect(threadsQuery).toMatchObject({ repositoryId: 'repo-a', pullRequestId: 42 })
    expect(threadsQuery).not.toHaveProperty('connectionId')

    expect(retained.error.value).toBeNull()
    expect(retained.files.value).toHaveLength(1)
    expect(retained.threads.value).toHaveLength(2)
    // Two comments on the file thread, none counted for the PR-level thread.
    expect(retained.commentCountForFile('src/foo.ts')).toBe(2)
    expect(retained.threadCountForFile('src/foo.ts')).toBe(1)
    expect(retained.prLevelThreads.value).toHaveLength(1)
    expect(retained.empty.value).toBe(false)
  })

  it('reports empty (not error) when retention returns no threads or files', async () => {
    getMock.mockResolvedValue(okResponse([]))

    const retained = useRetainedPrData(identity)
    await retained.load()

    expect(retained.empty.value).toBe(true)
    expect(retained.error.value).toBeNull()
  })

  it('lazily loads and adapts a file diff into the viewer shape', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/file-diff')) {
        return Promise.resolve(
          okResponse({ filePath: 'src/foo.ts', unifiedDiff: '@@ -1 +1 @@', changeType: 'Modified', isBinary: false }),
        )
      }
      return Promise.resolve(okResponse([]))
    })

    const retained = useRetainedPrData(identity)
    await retained.load()
    const diff = await retained.loadFileDiff('src/foo.ts', 'rev-1')

    expect(diff).not.toBeNull()
    expect(diff?.availability).toBe('Available')
    expect(diff?.unifiedDiff).toBe('@@ -1 +1 @@')
  })

  it('returns null (no error) when a file diff is not retained (404)', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/file-diff')) {
        return Promise.resolve({ data: undefined, error: undefined, response: { ok: false, status: 404 } })
      }
      return Promise.resolve(okResponse([]))
    })

    const retained = useRetainedPrData(identity)
    await retained.load()
    const diff = await retained.loadFileDiff('src/missing.ts')

    expect(diff).toBeNull()
  })

  it('surfaces an error when the threads request fails unexpectedly', async () => {
    getMock.mockImplementation((path: string) => {
      if (path.endsWith('/threads')) {
        return Promise.resolve({ data: undefined, error: { message: 'boom' }, response: { ok: false, status: 500 } })
      }
      return Promise.resolve(okResponse([]))
    })

    const retained = useRetainedPrData(identity)
    await retained.load()

    expect(retained.error.value).toBe('boom')
  })
})
