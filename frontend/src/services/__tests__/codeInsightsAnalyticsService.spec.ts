// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import {
  fetchConcentration,
  fetchFindings,
  fetchQuality,
  fetchRejectionReasons,
  fetchReviewerFindings,
  fetchTypesOverTime,
} from '@/services/codeInsightsAnalyticsService'

const getMock = vi.fn()

vi.mock('@/services/api', () => ({
  createAdminClient: () => ({ GET: (...args: unknown[]) => getMock(...args) }),
  getApiErrorMessage: (_error: unknown, fallback: string) => fallback,
}))

const SCOPE = { from: '2026-06-01', to: '2026-06-30' }

describe('codeInsightsAnalyticsService', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    getMock.mockResolvedValue({ data: {} })
  })

  it('omits the filters the caller left empty rather than sending blank ones', async () => {
    // A blank filter reaching the server would narrow to the empty string instead of to nothing, and quietly
    // return no rows for a filter nobody set.
    await fetchTypesOverTime({ ...SCOPE, clientId: null, repositoryId: '', filePath: null }, 'week')

    expect(getMock.mock.calls[0][1].params.query).toEqual({
      from: '2026-06-01',
      to: '2026-06-30',
      bucket: 'week',
    })
  })

  it('passes a client narrowing through when the caller chose one', async () => {
    await fetchTypesOverTime({ ...SCOPE, clientId: 'client-a' }, 'day')

    expect(getMock.mock.calls[0][1].params.query.clientId).toBe('client-a')
  })

  it('normalises a metric the contract types as fully optional', async () => {
    // Every ratio nullable and every count present, so no consumer has to defend against a partial payload.
    getMock.mockResolvedValue({ data: { correctness: [{ bucketStart: '2026-06-01', metric: {} }] } })

    const quality = await fetchQuality(SCOPE, 'week')

    expect(quality.correctness[0].metric.f1).toBeNull()
    expect(quality.correctness[0].metric.addressed).toBe(0)
    expect(quality.correctnessTotal.sampleSize).toBe(0)
    // A missing trend is untested rather than flat: flat is a test result and would read as holding steady.
    expect(quality.correctnessTrend.direction).toBe('insufficient')
    expect(quality.correctnessTrend.pValue).toBeNull()
    expect(quality.correctnessTrend.periods).toBe(0)
    expect(quality.minimumTrendPeriods).toBe(8)
  })

  it('carries the statistics behind a trend through untouched', async () => {
    getMock.mockResolvedValue({
      data: {
        correctnessTrend: { direction: 'declining', tau: -0.71, pValue: 0.013, slopePerPeriod: -0.018, periods: 9 },
        minimumTrendPeriods: 8,
      },
    })

    const quality = await fetchQuality(SCOPE, 'week')

    expect(quality.correctnessTrend).toEqual({
      direction: 'declining',
      tau: -0.71,
      pValue: 0.013,
      slopePerPeriod: -0.018,
      periods: 9,
    })
  })

  it('keeps an undefined ratio undefined instead of turning it into zero', async () => {
    getMock.mockResolvedValue({
      data: { correctnessTotal: { f1: null, precision: 0, sampleSize: 4 } },
    })

    const quality = await fetchQuality(SCOPE, 'week')

    expect(quality.correctnessTotal.f1).toBeNull()
    // Zero is a real value and must survive the normalisation unchanged.
    expect(quality.correctnessTotal.precision).toBe(0)
  })

  it('falls back to a threshold rather than to no threshold', async () => {
    getMock.mockResolvedValue({ data: {} })

    const quality = await fetchQuality(SCOPE, 'week')

    expect(quality.minimumSampleSize).toBeGreaterThan(0)
  })

  it('normalises a rejection-reason payload the contract types as fully optional', async () => {
    getMock.mockResolvedValue({ data: {} })

    const reasons = await fetchRejectionReasons(SCOPE)

    expect(reasons.reasons).toEqual([])
    expect(reasons.unclassified).toBe(0)
    expect(reasons.rejections).toBe(0)
    // A backend that predates the concern-class split answers without it, and the panel must render rather than
    // fail on a missing array.
    expect(reasons.byConcernClass).toEqual([])
  })

  it('sends a rejection reason on its own, without an outcome beside it', async () => {
    // A reason already implies its outcome. Sending both would narrow to their intersection and drop the rows
    // whose outcome the reason disagrees with.
    getMock.mockResolvedValue({ data: [] })

    await fetchReviewerFindings(SCOPE, { rejectionReason: 'OutOfScope' })

    const query = getMock.mock.calls[0][1].params.query
    expect(query.rejectionReason).toBe('OutOfScope')
    expect(query.disposition).toBeUndefined()
  })

  it('returns an empty list rather than null for the collection reads', async () => {
    getMock.mockResolvedValue({ data: null, error: undefined })

    await expect(fetchConcentration(SCOPE, 'repository')).rejects.toThrow()

    getMock.mockResolvedValue({ data: [] })
    await expect(fetchConcentration(SCOPE, 'repository')).resolves.toEqual([])
  })

  it('sends the type and outcome narrowings a drill-through was opened with', async () => {
    getMock.mockResolvedValue({ data: [] })

    await fetchFindings(SCOPE, { coreType: 'concurrency', disposition: 'falsePositive', limit: 25 })

    expect(getMock.mock.calls[0][1].params.query).toMatchObject({
      coreType: 'concurrency',
      disposition: 'falsePositive',
      limit: 25,
    })
  })

  it('surfaces a failed read as an error rather than as empty data', async () => {
    // "Not allowed" and "nothing collected" must not look the same to a caller.
    getMock.mockResolvedValue({ error: { error: 'Code Insights requires a commercial licence.' } })

    await expect(fetchQuality(SCOPE, 'week')).rejects.toThrow('Failed to load the quality metrics.')
  })
})
