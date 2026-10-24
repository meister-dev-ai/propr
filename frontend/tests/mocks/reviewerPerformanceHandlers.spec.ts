// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { mockPerformanceResponse, reviewerPerformanceHandlers } from '@/mocks/reviewerPerformance'
import type { PerformanceResponse, PerformanceSavedReport } from '@/services/reviewerPerformanceService'

const base = 'http://localhost/performance-test'
const server = setupServer(...reviewerPerformanceHandlers(base))
const query = { bucket: 'day', aggregation: 'cumulative', grouping: 'none', views: [{ from: '2026-09-19', to: '2026-10-18' }] }
async function post(path: string, body?: unknown): Promise<Response> {
  return fetch(`${base}/reviewer-performance/${path}`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  })
}
async function read(): Promise<PerformanceResponse> {
  return await (await post('ranges/query', query)).json() as PerformanceResponse
}

describe('reviewer performance mock recovery lifecycle', () => {
  it('filters joint risk evidence, preserves legacy Unassessed and does not invent risk-restricted recall', () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const raw = mockPerformanceResponse(query).views![0]!
    const last = (riskSeverities: string[] | null) => mockPerformanceResponse({ ...query,
      views: [{ ...query.views[0], riskSeverities }] }).views![0]!.series![0]!.points!.at(-1)!
    expect(raw.facets!.riskSeverities).toContainEqual({ id: 'Unknown', label: 'Unassessed' })
    const all = last(null), critical = last(['Critical']), unknown = last(['Unknown']), none = last([])
    expect(critical.overallPrecision!.counts!.generated!).toBeGreaterThan(0)
    expect(critical.overallPrecision!.counts!.generated!).toBeLessThan(all.overallPrecision!.counts!.generated!)
    expect(unknown.overallPrecision!.counts!.generated!).toBeGreaterThan(0)
    expect(none.overallPrecision!.counts!.generated).toBe(0)
    expect(critical.coverage!.candidatePullRequests).toBe(all.coverage!.candidatePullRequests)
    expect(critical.coverage!.recallSupported).toBe(false)
    expect(critical.unavailableReasons).toContain('miss-risk-assessment-unavailable')
    expect(critical.score!.summary!.recall).toBeUndefined()
    expect(critical.score!.summary!.f1).toBeUndefined()
    expect(critical.overallPrecision!.summary!.median).toBeGreaterThan(0)
    const grouped = mockPerformanceResponse({ ...query, grouping: 'riskSeverity', breakdown: { rows: 'impact', columns: 'likelihood' } }).views![0]!
    expect(grouped.series!.some(series => series.id === 'Unknown')).toBe(true)
    expect(grouped.breakdown!.some(cell => cell.rowId === 'Critical' && cell.columnId === 'Likely')).toBe(true)
    expect(grouped.breakdown!.every(cell => cell.measurement!.coverage!.recallSupported === false)).toBe(true)
    const matrixOnly = mockPerformanceResponse({ ...query, breakdown: { rows: 'impact', columns: 'likelihood' } }).views![0]!
    expect(matrixOnly.series![0]!.points!.at(-1)!.coverage!.recallSupported).toBe(true)
    expect(matrixOnly.breakdown!.every(cell => cell.measurement!.coverage!.recallSupported === false)).toBe(true)
    expect(matrixOnly.evidence!.projectionVersion).toBe(6)
  })

  it('keeps confidence scope, grouping and matrix evidence independent of risk severity', () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const response = mockPerformanceResponse({ ...query, grouping: 'confidence', breakdown: { rows: 'confidence', columns: 'impact' } })
    const view = response.views![0]!
    expect(view.facets!.confidences!.map(facet => facet.id)).toEqual(expect.arrayContaining(['Unknown', 'Low', 'Medium', 'High', 'Certain']))
    expect(view.series!.map(series => series.id)).toEqual(expect.arrayContaining(['Unknown', 'Low', 'Medium', 'High', 'Certain']))
    expect(view.breakdown!.every(cell => cell.measurement!.coverage!.recallSupported === false)).toBe(true)
    for (const confidences of [null, [], ['Certain']]) {
      const last = mockPerformanceResponse({ ...query, views: [{ ...query.views[0], confidences }] }).views![0]!.series![0]!.points!.at(-1)!
      if (confidences?.length === 0) expect(last.overallPrecision!.counts!.generated!).toBe(0)
      else expect(last.overallPrecision!.counts!.generated!).toBeGreaterThan(0)
      if (confidences?.length) expect(last.unavailableReasons).toContain('miss-risk-assessment-unavailable')
    }
  })

  beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
  afterAll(() => server.close())
  afterEach(() => vi.useRealTimers())

  it('scores qualified model cohorts and their matrix while keeping explicit unknown history', () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const response = mockPerformanceResponse({ ...query, grouping: 'model', breakdown: { rows: 'model', columns: 'client', date: '2026-10-18' } })
    const view = response.views![0]!
    const qualified = view.series!.find((series) => series.label === 'review-small')!
    expect(qualified.points!.at(-1)!.coverage!.recallSupported).toBe(true)
    expect(qualified.points!.at(-1)!.score!.summary!.f1!.median).toBeGreaterThan(0)
    const unknown = view.series!.find((series) => series.label === 'review-large')!
    expect(unknown.points!.at(-1)!.coverage!.excludedPullRequests).toBeGreaterThan(0)
    expect(unknown.points!.at(-1)!.score!.summary!.f1).toBeUndefined()
    expect(view.evidence!.modelEvidenceSchema).toBe('model-opportunity-v1')
    expect(view.breakdown!.some((cell) => cell.rowId === qualified.id && cell.measurement?.score?.summary?.f1?.median != null)).toBe(true)
  })

  it('deduplicates canonical missed concerns and source PRs for selected-model ensembles', () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const initial = mockPerformanceResponse(query).views![0]!
    const modelIds = ['review-small', 'review-next'].map((name) => initial.facets!.models!.find((model) => model.label === name)!.id!)
    const last = (models: string[]) => mockPerformanceResponse({ ...query, views: [{ ...query.views[0], models }] }).views![0]!.series![0]!.points!.at(-1)!
    const a = last([modelIds[0]!]), b = last([modelIds[1]!]), ensemble = last(modelIds)
    expect(ensemble.coverage!.recallSupported).toBe(true)
    expect(ensemble.score!.counts!.actedMisses!).toBeLessThan(a.score!.counts!.actedMisses! + b.score!.counts!.actedMisses!)
    expect(ensemble.coverage!.candidatePullRequests!).toBeLessThan(a.coverage!.candidatePullRequests! + b.coverage!.candidatePullRequests!)
  })

  it('retains numeric zero and inactive certified-zero model cases in the facets', () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const initial = mockPerformanceResponse(query).views![0]!
    const model = initial.facets!.models!.find((model) => model.label === 'review-next')!.id!
    const repository = initial.facets!.repositories![0]!.id!
    const view = mockPerformanceResponse({ ...query, aggregation: 'period', views: [{ ...query.views[0], models: [model], repositories: [repository] }] }).views![0]!
    expect(view.facets!.models!.some((facet) => facet.id === model)).toBe(true)
    expect(view.series![0]!.points!.some((point) => point.score?.summary?.f1?.median === 0 && point.score.summary.precision == null)).toBe(true)
    expect(view.series![0]!.points!.some((point) => point.coverage?.coveredPullRequests === 1 && point.score?.summary?.f1 == null
      && point.score?.counts?.outcomes?.total === 0 && point.score?.counts?.actedMisses === 0)).toBe(true)
  })

  it('advances queued requests through running to completion or failure while captures remain unchanged', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-18T12:00:00Z'))
    const before = await read()
    const catalog = before.views![0]!.exclusionCatalog!
    const retryable = catalog.find((entry) => entry.recoveryDisposition === 'retryable')!
    const repair = catalog.find((entry) => entry.recoveryDisposition === 'repair-required')!
    expect(retryable.aggregateId).not.toBe(repair.aggregateId)
    const capture = await (await post('reports', { id: 'recovery-capture', name: 'Captured', query })).json() as PerformanceSavedReport
    const capturedPayload = JSON.stringify(capture.response)
    for (const entry of [retryable, repair]) {
      expect((await post(`evidence/${entry.aggregateId}/retry`)).status).toBe(202)
      expect((await post(`evidence/${entry.aggregateId}/retry`)).status).toBe(409)
    }
    const queued = await read()
    expect(queued.views![0]!.exclusionCatalog!.find((entry) => entry.aggregateId === retryable.aggregateId)!.recoveryDisposition).toBe('queued')
    expect(queued.views![0]!.series![0]!.points!.at(-1)!.coverage!.excludedPullRequests).toBe(before.views![0]!.series![0]!.points!.at(-1)!.coverage!.excludedPullRequests)

    vi.setSystemTime(new Date('2026-10-18T12:00:06Z'))
    const running = await read()
    const active = running.views![0]!.exclusionCatalog!.find((entry) => entry.aggregateId === retryable.aggregateId)!
    expect(active.recoveryDisposition).toBe('in-progress')
    expect(active.lastAttemptAt).toBe('2026-10-18T12:00:05.000Z')
    expect(active.notBefore).toBe('2026-10-18T12:15:05.000Z')

    vi.setSystemTime(new Date('2026-10-18T12:00:16Z'))
    const finished = await read()
    expect(finished.views![0]!.exclusionCatalog!.some((entry) => entry.aggregateId === retryable.aggregateId)).toBe(false)
    const failed = finished.views![0]!.exclusionCatalog!.find((entry) => entry.aggregateId === repair.aggregateId)!
    expect(failed.recoveryDisposition).toBe('repair-required')
    expect(failed.lastAttemptAt).toBe('2026-10-18T12:00:05.000Z')
    expect(failed.lastRefreshReason).toBe('SourceCoordinatesUnavailable')
    expect((await post(`evidence/${repair.aggregateId}/retry`)).status).toBe(202)
    const reopened = await (await fetch(`${base}/reviewer-performance/reports/recovery-capture`)).json() as PerformanceSavedReport
    expect(JSON.stringify(reopened.response)).toBe(capturedPayload)
    expect(JSON.stringify(capture.response)).toBe(capturedPayload)
    expect(JSON.stringify(mockPerformanceResponse(query).views![0]!.exclusionCatalog)).toBe(JSON.stringify(mockPerformanceResponse(query).views![0]!.exclusionCatalog))
  })
})
