// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { effectScope, ref } from 'vue'
import { flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { usePrFindingMetadata } from '@/features/reviews/composables/usePrFindingMetadata'

const fetchMock = vi.fn()
vi.mock('@/services/codeInsightsAnalyticsService', () => ({ fetchFindings: (...args: unknown[]) => fetchMock(...args) }))

describe('PR finding metadata', () => {
  beforeEach(() => { vi.clearAllMocks() })

  it('loads complete bounded pages only when enabled and reuses the loaded PR', async () => {
    const identity = ref({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 })
    const enabled = ref(false)
    fetchMock.mockResolvedValueOnce(Array.from({ length: 200 }, (_, i) => ({ id: `f${i}` })))
      .mockResolvedValueOnce([{ id: 'f200' }])
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(identity, enabled, () => ['2025-01-01T00:00:00Z']))!
    await flushPromises()
    expect(fetchMock).not.toHaveBeenCalled()
    enabled.value = true
    await flushPromises()
    expect(metadata.findings.value).toHaveLength(201)
    // The Stats tab contains a page of recent jobs, so it cannot set the lower bound of a complete finding read.
    expect(fetchMock).toHaveBeenNthCalledWith(1, expect.objectContaining({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42, from: '1970-01-01' }), { limit: 200, offset: 0 })
    expect(fetchMock).toHaveBeenNthCalledWith(2, expect.anything(), { limit: 200, offset: 200 })
    enabled.value = false
    await flushPromises()
    enabled.value = true
    await flushPromises()
    expect(fetchMock).toHaveBeenCalledTimes(2)
    scope.stop()
  })

  it('ignores obsolete metadata after navigation and reports a retryable failure', async () => {
    let resolveFirst!: (value: unknown[]) => void
    fetchMock.mockReturnValueOnce(new Promise(resolve => { resolveFirst = resolve }))
      .mockRejectedValueOnce(new Error('Metadata unavailable'))
      .mockResolvedValueOnce([{ id: 'current' }])
    const identity = ref({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 })
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(identity, () => true, () => []))!
    identity.value = { ...identity.value, pullRequestId: 43 }
    await flushPromises()
    resolveFirst([{ id: 'obsolete' }])
    await flushPromises()
    expect(metadata.findings.value).toEqual([])
    expect(metadata.error.value).toContain('Metadata unavailable')
    await metadata.load()
    expect(metadata.findings.value).toEqual([{ id: 'current' }])
    expect(metadata.error.value).toBeNull()
    scope.stop()
  })

  it('collects overlapping pages without duplicating findings or rejecting valid progress', async () => {
    fetchMock.mockResolvedValueOnce(Array.from({ length: 200 }, (_, i) => ({ id: `f${i}` })))
      .mockResolvedValueOnce(Array.from({ length: 200 }, (_, i) => ({ id: `f${i + 100}` })))
      .mockResolvedValueOnce([{ id: 'last' }])
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(
      () => ({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 }),
      () => true, () => [],
    ))!
    await flushPromises()
    expect(metadata.findings.value.map(finding => finding.id)).toEqual([
      ...Array.from({ length: 300 }, (_, i) => `f${i}`), 'last',
    ])
    expect(metadata.error.value).toBeNull()
    expect(fetchMock).toHaveBeenNthCalledWith(3, expect.anything(), { limit: 200, offset: 400 })
    scope.stop()
  })

  it('reports fully repeated pages as retryable instead of returning incomplete metadata', async () => {
    const page = Array.from({ length: 200 }, (_, i) => ({ id: `f${i}` }))
    fetchMock.mockResolvedValueOnce(page).mockResolvedValueOnce(page).mockResolvedValueOnce([{ id: 'current' }])
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(
      () => ({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 }),
      () => true, () => [],
    ))!
    await flushPromises()
    expect(metadata.findings.value).toEqual([])
    expect(metadata.error.value).toContain('pagination did not advance')
    await metadata.load()
    expect(metadata.findings.value).toEqual([{ id: 'current' }])
    expect(metadata.error.value).toBeNull()
    scope.stop()
  })

  it.each([false, true])('refreshes metadata when review history changes, including during a request (pending: %s)', async pending => {
    let resolveFirst!: (value: unknown[]) => void
    fetchMock.mockReturnValueOnce(new Promise(resolve => { resolveFirst = resolve }))
      .mockResolvedValueOnce([{ id: 'current' }])
    const dates = ref(['2025-01-01T10:00:00Z'])
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(
      () => ({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 }),
      () => true, dates,
    ))!
    if (!pending) {
      resolveFirst([{ id: 'earlier' }])
      await flushPromises()
    }
    dates.value = [...dates.value, '2025-01-01T11:00:00Z']
    await flushPromises()
    expect(fetchMock).toHaveBeenCalledTimes(2)
    if (pending) {
      resolveFirst([{ id: 'obsolete' }])
      await flushPromises()
    }
    expect(metadata.findings.value).toEqual([{ id: 'current' }])
    expect(metadata.loading.value).toBe(false)
    expect(metadata.error.value).toBeNull()
    scope.stop()
  })

  it('extends the refreshed date scope to include a later review date', async () => {
    fetchMock.mockResolvedValueOnce([]).mockResolvedValueOnce([{ id: 'later' }])
    const dates = ref<string[]>([])
    const today = new Date().toISOString().slice(0, 10)
    const later = new Date(Date.now() + 366 * 24 * 60 * 60 * 1000).toISOString()
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(
      () => ({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 }),
      () => true, dates,
    ))!
    await flushPromises()
    expect(fetchMock).toHaveBeenNthCalledWith(1, expect.objectContaining({ to: today }), { limit: 200, offset: 0 })
    dates.value = [later]
    await flushPromises()
    expect(fetchMock).toHaveBeenNthCalledWith(2, expect.objectContaining({ to: later.slice(0, 10) }), { limit: 200, offset: 0 })
    expect(metadata.findings.value).toEqual([{ id: 'later' }])
    scope.stop()
  })

  it('keeps the last successful metadata for the same PR while exposing refresh progress and failure', async () => {
    let rejectRefresh!: (error: Error) => void
    fetchMock.mockResolvedValueOnce([{ id: 'known' }])
      .mockReturnValueOnce(new Promise((_resolve, reject) => { rejectRefresh = reject }))
    const dates = ref(['2025-01-01T10:00:00Z'])
    const scope = effectScope()
    const metadata = scope.run(() => usePrFindingMetadata(
      () => ({ clientId: 'client-1', repositoryId: 'repo-1', pullRequestId: 42 }),
      () => true, dates,
    ))!
    await flushPromises()
    dates.value = [...dates.value, '2025-01-01T11:00:00Z']
    await flushPromises()
    expect(metadata.findings.value).toEqual([{ id: 'known' }])
    expect(metadata.loading.value).toBe(true)
    expect(metadata.error.value).toBeNull()
    rejectRefresh(new Error('Refresh unavailable'))
    await flushPromises()
    expect(metadata.findings.value).toEqual([{ id: 'known' }])
    expect(metadata.loading.value).toBe(false)
    expect(metadata.error.value).toBe('Refresh unavailable')
    scope.stop()
  })
})
