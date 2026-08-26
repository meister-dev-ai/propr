// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { beforeEach, describe, expect, it, vi } from 'vitest'

const getLicensingSummaryMock = vi.fn()
const activateLicenseMock = vi.fn()
const removeLicenseMock = vi.fn()
const setCapabilityOverrideMock = vi.fn()
const getLicenseActivationHistoryMock = vi.fn()

vi.mock('@/services/licensingService', () => ({
  getLicensingSummary: getLicensingSummaryMock,
  activateLicense: activateLicenseMock,
  removeLicense: removeLicenseMock,
  setCapabilityOverride: setCapabilityOverrideMock,
  getLicenseActivationHistory: getLicenseActivationHistoryMock,
}))

function summary(overrides: Record<string, unknown> = {}) {
  return {
    edition: 'commercial',
    activatedAt: '2026-01-02T09:15:00Z',
    capabilities: [],
    stage: 'active',
    notBefore: '2026-01-01T00:00:00Z',
    warningStartsAt: null,
    expiresAt: '2026-12-01T00:00:00Z',
    graceEndsAt: '2026-12-15T00:00:00Z',
    daysRemaining: 100,
    licensee: 'Contoso Engineering',
    licenseId: 'c9f5c9a2',
    limits: [],
    licensingIdentity: '5f2c0d1e',
    authorOverage: null,
    authorPeakMonth: null,
    ...overrides,
  }
}

async function loadComposable() {
  const { useLicensing } = await import('@/composables/useLicensing')

  return useLicensing()
}

describe('useLicensing', () => {
  beforeEach(async () => {
    vi.clearAllMocks()
    getLicensingSummaryMock.mockResolvedValue(summary())
    getLicenseActivationHistoryMock.mockResolvedValue([])
    activateLicenseMock.mockResolvedValue(summary())
    removeLicenseMock.mockResolvedValue(undefined)
    setCapabilityOverrideMock.mockResolvedValue(summary())

    // The state is a module singleton, so one test's license would otherwise be the next test's starting
    // point.
    ;(await loadComposable()).reset()
  })

  it('loads the summary once and serves the cached answer afterwards', async () => {
    const licensing = await loadComposable()

    await licensing.load()
    await licensing.load()

    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(1)
    expect(licensing.summary.value?.licensee).toBe('Contoso Engineering')
  })

  it('re-reads when the caller forces it', async () => {
    const licensing = await loadComposable()

    await licensing.load()
    await licensing.load(true)

    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(2)
  })

  // Two components mount within a tick of each other. A second request would let the first request's
  // completion handler clear the handle belonging to the second.
  it('serves two callers that ask at the same time from one request', async () => {
    const licensing = await loadComposable()

    await Promise.all([licensing.load(), licensing.load()])

    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(1)
  })

  it('leaves the state empty when the read fails', async () => {
    getLicensingSummaryMock.mockRejectedValue(new Error('offline'))
    const licensing = await loadComposable()

    await licensing.load()

    expect(licensing.summary.value).toBeNull()
    expect(licensing.loading.value).toBe(false)
  })

  // Only the three stages that call for an action put a notice in front of an administrator.
  it.each([
    ['none', null],
    ['notYetValid', null],
    ['active', null],
    ['warning', 'warning'],
    ['grace', 'grace'],
    ['reverted', 'reverted'],
  ])('reports the notice stage for %s', async (stage, expected) => {
    getLicensingSummaryMock.mockResolvedValue(summary({ stage }))
    const licensing = await loadComposable()

    await licensing.load()

    expect(licensing.noticeStage.value).toBe(expected)
  })

  // The re-read is what carries the current counts beside the limits and the stage the new license puts the
  // installation in, so a mutation does not keep whatever the mutation endpoint answered with.
  it('re-reads the summary after an activation', async () => {
    const licensing = await loadComposable()

    await licensing.activate('a-license-document')

    expect(activateLicenseMock).toHaveBeenCalledWith('a-license-document')
    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(1)
    expect(getLicenseActivationHistoryMock).toHaveBeenCalledTimes(1)
  })

  it('re-reads the summary after a removal', async () => {
    const licensing = await loadComposable()

    await licensing.remove()

    expect(removeLicenseMock).toHaveBeenCalledTimes(1)
    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(1)
  })

  it('re-reads the summary after an override changes', async () => {
    const licensing = await loadComposable()

    await licensing.setOverride('budgeting', 'disabled')

    expect(setCapabilityOverrideMock).toHaveBeenCalledWith('budgeting', 'disabled')
    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(1)
  })

  // The license is already written when the history read runs, so raising here would tell an operator the
  // activation failed while the installation runs on the new license. The history list owns that report.
  it('does not fail an activation because the history read failed', async () => {
    getLicenseActivationHistoryMock.mockRejectedValue(new Error('history is unavailable'))
    const licensing = await loadComposable()

    await licensing.activate('a-license-document')

    expect(licensing.summary.value?.licensee).toBe('Contoso Engineering')
  })

  // A failed history refresh after a mutation leaves the list showing records that may no longer match what
  // was just written. The composable surfaces that so the list can render a stale indicator.
  it('marks the history stale when a mutation-triggered refresh fails', async () => {
    getLicenseActivationHistoryMock.mockRejectedValueOnce(new Error('history is unavailable'))
    const licensing = await loadComposable()

    await licensing.activate('a-license-document')

    expect(licensing.historyStale.value).toBe(true)
  })

  // The stale marker comes from a background refresh that failed; a successful read clears it, so the
  // warning does not outlive the condition it describes.
  it('clears the stale marker when a subsequent history read succeeds', async () => {
    getLicenseActivationHistoryMock
      .mockRejectedValueOnce(new Error('history is unavailable'))
      .mockResolvedValueOnce([])
    const licensing = await loadComposable()

    await licensing.activate('a-license-document')
    expect(licensing.historyStale.value).toBe(true)

    await licensing.loadHistory()
    expect(licensing.historyStale.value).toBe(false)
  })

  // A newer read has already answered, so the failure of the older one describes a read nobody waits on.
  // Raising it would have the caller mark records stale that the newer read has just loaded.
  it('does not raise a history read that a newer read superseded', async () => {
    let failFirstRead: (reason: Error) => void = () => {}
    getLicenseActivationHistoryMock
      .mockReturnValueOnce(new Promise((_resolve, reject) => { failFirstRead = reject }))
      .mockResolvedValueOnce([{ action: 'activated', occurredAt: '2026-01-01T00:00:00Z' }])

    const licensing = await loadComposable()
    const superseded = licensing.loadHistory()
    await licensing.loadHistory()

    failFirstRead(new Error('history is unavailable'))

    await expect(superseded).resolves.toBeUndefined()
    expect(licensing.history.value).toEqual([{ action: 'activated', occurredAt: '2026-01-01T00:00:00Z' }])
    expect(licensing.historyStale.value).toBe(false)
  })

  // A read still in flight when the session ended fails after it. Raising then would let the next session's
  // caller mark records stale that this session's failure has nothing to say about.
  it('does not raise a history read that a reset superseded', async () => {
    let failRead: (reason: Error) => void = () => {}
    getLicenseActivationHistoryMock.mockReturnValue(new Promise((_resolve, reject) => { failRead = reject }))

    const licensing = await loadComposable()
    const superseded = licensing.loadHistory()

    licensing.reset()
    failRead(new Error('history is unavailable'))

    await expect(superseded).resolves.toBeUndefined()
    expect(licensing.historyStale.value).toBe(false)
  })

  it('does not fail a removal because the history read failed', async () => {
    getLicenseActivationHistoryMock.mockRejectedValue(new Error('history is unavailable'))
    const licensing = await loadComposable()

    await licensing.remove()

    expect(removeLicenseMock).toHaveBeenCalledTimes(1)
  })

  // A read already in flight when the session ends finishes after it. Writing its answer would hand the next
  // sign-in the installation the previous one was looking at.
  it('discards a read that comes back after a reset', async () => {
    let releaseRead: (value: unknown) => void = () => {}
    getLicensingSummaryMock.mockReturnValue(new Promise((resolve) => {
      releaseRead = resolve
    }))

    const licensing = await loadComposable()
    const pending = licensing.load()

    licensing.reset()
    releaseRead(summary({ licensee: 'The previous session' }))
    await pending

    expect(licensing.summary.value).toBeNull()
  })

  it('discards history that comes back after a reset', async () => {
    let releaseHistory: (value: unknown) => void = () => {}
    getLicenseActivationHistoryMock.mockReturnValue(new Promise((resolve) => {
      releaseHistory = resolve
    }))

    const licensing = await loadComposable()
    const pending = licensing.loadHistory()

    licensing.reset()
    releaseHistory([{ action: 'activated', occurredAt: '2026-01-01T00:00:00Z' }])
    await pending

    expect(licensing.history.value).toEqual([])
  })

  it('keeps the most recent history response when reads complete out of order', async () => {
    let releaseFirst: (value: unknown) => void = () => {}
    let releaseSecond: (value: unknown) => void = () => {}
    getLicenseActivationHistoryMock
      .mockReturnValueOnce(new Promise((resolve) => { releaseFirst = resolve }))
      .mockReturnValueOnce(new Promise((resolve) => { releaseSecond = resolve }))

    const licensing = await loadComposable()
    const first = licensing.loadHistory()
    const second = licensing.loadHistory()

    releaseSecond([{ action: 'replaced', occurredAt: '2026-02-01T00:00:00Z' }])
    await second
    releaseFirst([{ action: 'activated', occurredAt: '2026-01-01T00:00:00Z' }])
    await first

    expect(licensing.history.value).toEqual([{ action: 'replaced', occurredAt: '2026-02-01T00:00:00Z' }])
  })

  // Sign-out unmounts the chrome that holds this state. The next session must not render the previous
  // session's installation.
  it('clears the state on reset', async () => {
    const licensing = await loadComposable()

    await licensing.load()
    await licensing.loadHistory()
    licensing.reset()

    expect(licensing.summary.value).toBeNull()
    expect(licensing.history.value).toEqual([])
    expect(licensing.noticeStage.value).toBeNull()

    await licensing.load()

    expect(getLicensingSummaryMock).toHaveBeenCalledTimes(2)
  })

  // The author-overage notice is the only path that reads authorOverage off the summary; without coverage
  // here a regression in the computed would go unnoticed.
  it('surfaces the author overage notice when the summary reports one', async () => {
    getLicensingSummaryMock.mockResolvedValue(summary({
      authorOverage: { licensedCount: 5, observedCount: 7, isInOverage: true },
      authorPeakMonth: { month: '2026-03-01', authorCount: 7 },
    }))
    const licensing = await loadComposable()

    await licensing.load()

    expect(licensing.authorOverageNotice.value).toEqual({
      licensedCount: 5,
      observedCount: 7,
      isInOverage: true,
    })
  })

  it('returns no author overage notice when the summary is not in overage', async () => {
    getLicensingSummaryMock.mockResolvedValue(summary({
      authorOverage: { licensedCount: 5, observedCount: 3, isInOverage: false },
      authorPeakMonth: null,
    }))
    const licensing = await loadComposable()

    await licensing.load()

    expect(licensing.authorOverageNotice.value).toBeNull()
  })
})
