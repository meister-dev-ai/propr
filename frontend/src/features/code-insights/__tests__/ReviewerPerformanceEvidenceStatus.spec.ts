// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { PerformanceQuery, PerformanceResponse, PerformanceView } from '@/services/reviewerPerformanceService'
import ReviewerPerformanceWorkspace from '../components/ReviewerPerformanceWorkspace.vue'

vi.mock('../components/ReviewerPerformanceSelect.vue', async () => {
  const { performanceSelectStub } = await import('./performanceControlStubs')
  return { default: performanceSelectStub }
})

const query = vi.fn()
const list = vi.fn()
const open = vi.fn()
vi.mock('@/services/reviewerPerformanceService', () => ({
  queryPerformance: (...args: unknown[]) => query(...args),
  listPerformanceReports: () => list(),
  openPerformanceReport: (...args: unknown[]) => open(...args),
  savePerformanceReport: vi.fn(),
  deletePerformanceReport: vi.fn(),
  listPerformanceClients: async () => [],
}))

const updatedAt = '2026-09-01T08:30:00Z'
const capturedAt = '2026-09-03T10:00:00Z'
const view: PerformanceView = {
  scope: { from: '2026-09-01', to: '2026-09-02' },
  facets: { clients: [], repositories: [], models: [], types: [], qualifiers: [] },
  series: [{ id: 'all', label: 'All selected evidence', points: [{ date: '2026-09-01' }] }],
  breakdown: [],
  aggregateCells: 56789,
  evidence: { newestProjectionAt: updatedAt, pendingSourceAggregates: 98761 },
}
const response: PerformanceResponse = {
  calculationVersion: 'reviewer-performance-v1',
  capturedAt,
  query: { bucket: 'day', aggregation: 'cumulative', grouping: 'none', views: [view.scope!] },
  views: [view],
  premiseIds: [],
}

describe('reviewer performance evidence update status', () => {
  let wrapper: VueWrapper | undefined

  beforeEach(() => {
    query.mockReset()
    list.mockReset()
    open.mockReset()
    query.mockResolvedValue(response)
    list.mockResolvedValue([])
  })

  afterEach(() => {
    wrapper?.unmount()
    wrapper = undefined
  })

  it('warns that pending evidence updates may change current scores without displaying counters', async () => {
    wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()

    const status = wrapper.get('[aria-label="Evidence updates"]')
    expect(status.text()).toContain(
      'Evidence updates are pending. Scores may change as updates are processed.',
    )
    expect(status.text()).not.toMatch(/56789|98761|aggregate|projection|joint cells/i)
  })

  it('identifies each comparison view in its evidence update announcement', async () => {
    query.mockImplementation(async (request: PerformanceQuery) => ({
      ...response,
      query: request,
      views: request.views?.map((scope, index) => ({
        ...view,
        scope,
        evidence: index === 0
          ? { newestProjectionAt: updatedAt, pendingSourceAggregates: 1 }
          : { newestProjectionAt: '2026-09-02T12:00:00Z', pendingSourceAggregates: 0 },
      })),
    }))
    wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="comparison"] input[type="checkbox"]').setValue(true)
    await flushPromises()

    const a = wrapper.get('[aria-label="View A evidence updates"]')
    const b = wrapper.get('[aria-label="View B evidence updates"]')
    expect(a.text()).toContain('View A')
    expect(a.text()).toContain('Evidence updates are pending.')
    expect(a.get('time').attributes('datetime')).toBe(updatedAt)
    expect(b.text()).toContain('View B')
    expect(b.text()).not.toContain('Evidence updates are pending.')
    expect(b.get('time').attributes('datetime')).toBe('2026-09-02T12:00:00Z')
  })

  it('describes pending updates at snapshot capture while preserving saved scores', async () => {
    query.mockResolvedValue({
      ...response,
      views: [{ ...view, evidence: { newestProjectionAt: updatedAt, pendingSourceAggregates: 0 } }],
    })
    list.mockResolvedValue([{ id: 'stored', name: 'Saved sample' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Saved sample', capturedAt: '2026-09-04T12:00:00Z' },
      response,
      compatibleVersion: true,
    })
    wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()

    const status = wrapper.get('[aria-label="Evidence updates"]')
    expect(status.text()).toContain(
      'Evidence updates were pending at capture. Saved scores remain unchanged.',
    )
    expect(status.text()).not.toContain('Scores may change')
    expect(status.text()).not.toMatch(/98761|aggregate|projection|joint cells/i)
    expect(query).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-test="aggregation"]').attributes('disabled')).toBeDefined()
  })

  it('shows the evidence update date separately from a newer report capture date', async () => {
    list.mockResolvedValue([{ id: 'stored', name: 'Saved sample' }])
    open.mockResolvedValue({
      report: { id: 'stored', name: 'Saved sample', capturedAt: '2026-09-04T12:00:00Z' },
      response,
      compatibleVersion: true,
    })
    wrapper = mount(ReviewerPerformanceWorkspace)
    await flushPromises()
    await wrapper.get('[data-test="report-select"]').setValue('stored')
    await flushPromises()

    const status = wrapper.get('[aria-label="Evidence updates"]')
    const date = status.get('time')
    expect(status.text()).toContain('Evidence last updated')
    expect(date.attributes('datetime')).toBe(updatedAt)
    expect(date.text()).toMatch(/1 (?:Sep|Sept) 2026, 08:30 UTC/)
    expect(date.text()).not.toBe(updatedAt)
    expect(status.text()).not.toContain(capturedAt)
    expect(status.text()).not.toContain('2026-09-04')
    expect(wrapper.get('.performance-methods').text()).toContain(`Report captured ${capturedAt}.`)
  })

  it.each([undefined, null, '', 'invalid-date'])(
    'keeps an unavailable evidence update time explicit for %s',
    async (timestamp) => {
      query.mockResolvedValue({
        ...response,
        views: [{
          ...view,
          evidence: { newestProjectionAt: timestamp, pendingSourceAggregates: 0 },
        }],
      })
      wrapper = mount(ReviewerPerformanceWorkspace)
      await flushPromises()

      const status = wrapper.get('[aria-label="Evidence updates"]')
      expect(status.text()).toContain('Evidence update time unavailable.')
      expect(status.find('time').exists()).toBe(false)
      expect(status.text()).not.toContain('Scores may change')
      expect(status.text()).not.toContain(capturedAt)
    },
  )
})
