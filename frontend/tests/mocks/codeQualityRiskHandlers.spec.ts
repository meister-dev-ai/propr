// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { setupServer } from 'msw/node'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { handlers } from '@/mocks/handlers'
import { API_BASE_URL } from '@/services/apiBase'
import type { CodeInsightFinding, CodeInsightTypeSeries } from '@/services/codeInsightsAnalyticsService'

const server = setupServer(...handlers)
async function distribution(query: string): Promise<CodeInsightTypeSeries> {
  const response = await fetch(`${API_BASE_URL}/code-quality/risk-over-time?${query}`)
  expect(response.status).toBe(200)
  return await response.json() as CodeInsightTypeSeries
}
async function findings(query: string): Promise<CodeInsightFinding[]> {
  const response = await fetch(`${API_BASE_URL}/code-quality/findings?${query}`)
  expect(response.status).toBe(200)
  return await response.json() as CodeInsightFinding[]
}

describe('Code Quality risk mock scopes', () => {
  beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
  afterAll(() => server.close())

  it('returns no findings for a window outside their observation dates', async () => {
    const result = await distribution('from=2026-08-01&to=2026-08-31')
    expect(result).toEqual({ totalFindings: 0, keys: [], points: [] })
  })

  it.each([
    ['day', '2026-07-19', '2026-07-22'],
    ['week', '2026-07-13', '2026-07-20'],
    ['month', '2026-07-01', '2026-07-01'],
  ])('buckets observed findings by %s', async (bucket, unknownDate, criticalDate) => {
    const result = await distribution(`from=2026-07-01&to=2026-07-31&bucket=${bucket}`)
    expect(result.totalFindings).toBe(3)
    expect(result.points).toContainEqual({ bucketStart: unknownDate, key: 'Unknown', count: 1 })
    expect(result.points).toContainEqual({ bucketStart: criticalDate, key: 'Critical', count: 1 })
    expect(result.points.reduce((sum, point) => sum + point.count, 0)).toBe(result.totalFindings)
  })

  it('intersects the inclusive date range with the selected risk', async () => {
    const result = await distribution('from=2026-07-19&to=2026-07-19&riskSeverity=critical')
    expect(result).toEqual({ totalFindings: 0, keys: [], points: [] })
    const matching = await distribution('from=2026-07-22&to=2026-07-22&riskSeverity=critical')
    expect(matching.totalFindings).toBe(1)
  })

  it('applies the same confidence intersection to distribution and paged finding requests', async () => {
    const scope = 'from=2026-07-01&to=2026-07-31&confidence=certain&impact=critical&likelihood=likely'
    const result = await distribution(`${scope}&dimension=confidence`)
    expect(result).toMatchObject({ totalFindings: 1, keys: ['Certain'] })
    const findings = await (await fetch(`${API_BASE_URL}/code-quality/findings?${scope}&offset=0&limit=1`)).json() as { riskAssessment: { confidence: string } }[]
    expect(findings).toHaveLength(1)
    expect(findings[0]!.riskAssessment.confidence).toBe('certain')
    expect((await distribution(scope.replace('confidence=certain', 'confidence=low'))).totalFindings).toBe(0)
  })

  it.each([
    ['from=2026-08-01&to=2026-08-31', []],
    ['clientId=other-client', []],
    ['repositoryId=other-repository', []],
    ['filePath=src/Payments/LedgerWriter.cs', ['finding-2']],
    ['pullRequestId=4790', ['finding-3']],
    ['coreType=security', ['finding-3']],
  ])('applies non-risk scope %s to findings and aggregate reads', async (query, expectedIds) => {
    expect((await findings(query)).map(finding => finding.id)).toEqual(expectedIds)
    const response = await fetch(`${API_BASE_URL}/code-quality/types-over-time?${query}`)
    expect(response.status).toBe(200)
    expect((await response.json() as CodeInsightTypeSeries).totalFindings).toBe(expectedIds.length)
  })

  it.each(['', 'riskSeverity=low'])('normalizes integer pagination with scope %s', async (scope) => {
    const first = await findings(`${scope}&offset=-1&limit=0`)
    expect(first.map(finding => finding.id)).toEqual(['finding-1'])
  })

  it('returns the requested finding page without a risk filter', async () => {
    expect((await findings('')).map(finding => finding.id)).toEqual(['finding-1', 'finding-2', 'finding-3'])
    expect((await findings('offset=1&limit=1')).map(finding => finding.id)).toEqual(['finding-2'])
    expect((await findings('offset=3&limit=1'))).toEqual([])
  })

  it.each(['offset=invalid', 'limit=invalid', 'offset=1.5', 'limit=1.0', 'limit=1e2', 'limit=', 'limit=2147483648', 'offset=-2147483649'])('rejects malformed integer pagination %s', async (query) => {
    const response = await fetch(`${API_BASE_URL}/code-quality/findings?${query}`)
    expect(response.status).toBe(400)
  })
})
