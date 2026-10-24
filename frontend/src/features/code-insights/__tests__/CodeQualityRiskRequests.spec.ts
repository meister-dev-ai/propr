// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useCodeQualityViewModel } from '../composables/useCodeQualityViewModel'

const { risk, types, findings } = vi.hoisted(() => ({ risk: vi.fn(), types: vi.fn(), findings: vi.fn() }))
vi.mock('@/services/codeInsightsAnalyticsService', () => ({
  fetchRiskOverTime: risk,
  fetchTypesOverTime: types,
  fetchConcentration: vi.fn().mockResolvedValue([]), fetchFindings: findings,
  fetchHotspots: vi.fn().mockResolvedValue({ files: [] }), fetchSurvival: vi.fn().mockResolvedValue({ pullRequests: [] }),
  fetchRepositoryDirectory: vi.fn().mockResolvedValue({ rows: [] }),
}))
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const series = (key: string) => ({ totalFindings: 1, keys: [key], points: [{ bucketStart: '2026-10-01', key, count: 1 }] })

describe('Code Quality risk distributions', () => {
  beforeEach(() => {
    risk.mockReset()
    findings.mockReset().mockResolvedValue([])
    types.mockReset().mockResolvedValue({ points: [], keys: [], totalFindings: 0 })
  })

  it('keeps confidence in distribution requests and confidence drills with other assessment selections', async () => {
    risk.mockResolvedValue(series('Certain'))
    const vm = useCodeQualityViewModel({ clientId: 'client', repositoryId: 'repo', pullRequestId: 42 })
    vm.riskDimension.value = 'confidence'
    vm.riskFilters.value = { impact: 'high', likelihood: 'unlikely', confidence: 'certain' }
    await vm.loadRisk()
    expect(risk.mock.lastCall).toEqual([expect.objectContaining({ confidence: 'certain', impact: 'high', likelihood: 'unlikely' }), 'day', 'confidence'])
    await vm.openDrill({ title: 'High confidence', confidence: 'high' })
    expect(findings.mock.lastCall?.[0]).toMatchObject({ confidence: 'high', impact: 'high', likelihood: 'unlikely', pullRequestId: 42 })
  })

  it('refreshes the risk scope even when another quality read fails', async () => {
    risk.mockResolvedValueOnce(series('Critical')).mockResolvedValueOnce(series('Low'))
    const vm = useCodeQualityViewModel({ repositoryId: 'repo', pullRequestId: 42 })
    vm.section.value = 'risk'
    await vm.load()
    vm.from.value = '2026-09-01'
    types.mockRejectedValueOnce(new Error('Types unavailable.'))

    await vm.load()

    expect(risk).toHaveBeenCalledTimes(2)
    expect(risk.mock.lastCall?.[0].from).toBe('2026-09-01')
    expect(vm.riskSeries.value).toEqual(series('Low'))
    expect(vm.error.value).toBe('Types unavailable.')
  })

  it('invalidates pending risk reads and drill evidence before reloading a new repository scope', async () => {
    const earlier = deferred<ReturnType<typeof series>>()
    risk.mockReturnValueOnce(earlier.promise)
    const vm = useCodeQualityViewModel({ repositoryId: 'repo', pullRequestId: 42 })
    const first = vm.loadRisk()
    await vm.openDrill({ title: 'Critical risk', riskSeverity: 'critical' })
    vm.repositoryId.value = 'other-repo'
    types.mockRejectedValueOnce(new Error('Types unavailable.'))

    await vm.load()
    earlier.resolve(series('Critical'))
    await first

    expect(vm.riskSeries.value.points).toEqual([])
    expect(vm.drill.value).toBeNull()
    expect(vm.riskLoading.value).toBe(false)
  })

  it('keeps the latest dimension and scope when an earlier request finishes later', async () => {
    const earlier = deferred<ReturnType<typeof series>>(), latest = deferred<ReturnType<typeof series>>()
    risk.mockReturnValueOnce(earlier.promise).mockReturnValueOnce(latest.promise)
    const vm = useCodeQualityViewModel({ clientId: 'client', repositoryId: 'repo', pullRequestId: 42 })
    const first = vm.loadRisk()
    vm.riskDimension.value = 'impact'
    vm.riskFilters.value = { likelihood: 'likely' }
    const second = vm.loadRisk()
    expect(vm.riskSeries.value.points).toEqual([])
    expect(vm.riskLoading.value).toBe(true)
    latest.resolve(series('Critical'))
    await second
    earlier.resolve(series('Unknown'))
    await first
    expect(vm.riskSeries.value).toEqual(series('Critical'))
    expect(risk.mock.lastCall).toEqual([expect.objectContaining({ likelihood: 'likely', pullRequestId: 42 }), 'day', 'impact'])
  })

  it('clears stale distributions on failure and ignores an earlier rejected request', async () => {
    const earlier = deferred<ReturnType<typeof series>>()
    risk.mockReturnValueOnce(earlier.promise).mockResolvedValueOnce(series('High')).mockRejectedValueOnce(new Error('Distribution unavailable.'))
    const vm = useCodeQualityViewModel({ repositoryId: 'repo', pullRequestId: 42 })
    const first = vm.loadRisk()
    await vm.loadRisk()
    earlier.reject(new Error('Stale failure.'))
    await first
    expect(vm.riskSeries.value).toEqual(series('High'))
    expect(vm.riskError.value).toBeNull()
    await vm.loadRisk()
    expect(vm.riskSeries.value.points).toEqual([])
    expect(vm.riskError.value).toBe('Distribution unavailable.')
    expect(vm.riskLoading.value).toBe(false)
  })
})
